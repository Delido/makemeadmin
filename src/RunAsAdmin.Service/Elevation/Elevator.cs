using System.ComponentModel;
using System.DirectoryServices.AccountManagement;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using RunAsAdmin.Service.Native;
using static RunAsAdmin.Service.Native.Win32;

namespace RunAsAdmin.Service.Elevation;

public readonly record struct LaunchResult(bool Ok, int ProcessId, string Detail);

/// <summary>
/// Startet ein Programm erhöht in der interaktiven Sitzung des angemeldeten
/// Benutzers. Das Benutzerkonto wird dafür nur für den Moment des Starts in die
/// lokale Administratorengruppe aufgenommen und sofort wieder entfernt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Elevator
{
    /// <summary>
    /// Startet <paramref name="exePath"/> erhöht für den Benutzer der aktiven
    /// Konsolensitzung. <paramref name="onStep"/> meldet Zwischenschritte fürs Protokoll.
    /// </summary>
    public LaunchResult LaunchElevated(string exePath, string? arguments, string? workingDir,
        Action<string> onStep)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            return new(false, 0, "Keine aktive Benutzersitzung gefunden.");

        // SeTcbPrivilege aktivieren – WTSQueryUserToken benötigt es aktiviert.
        // LocalSystem besitzt es, aber es ist nicht automatisch im Token eingeschaltet.
        EnablePrivilege(SE_TCB_NAME, onStep);

        if (!WTSQueryUserToken(sessionId, out var userToken))
            return new(false, 0, $"WTSQueryUserToken fehlgeschlagen: {Marshal.GetLastPInvokeError()}");

        string? samAccount = null;
        var addedToGroup = false;
        try
        {
            var user = ResolveUser(userToken);
            samAccount = user.Account;
            onStep($"Angemeldeter Benutzer: {user.Full} (Sitzung {sessionId})");

            // Kind kurz in die Administratorengruppe nehmen, damit das verknüpfte
            // (vollwertige) Token Admin-Rechte trägt.
            addedToGroup = AddToAdministrators(user.Sid, out var groupDetail);
            onStep(groupDetail);

            using var linked = GetLinkedElevatedToken(userToken);

            // Gruppenmitgliedschaft wird sofort wieder entzogen. Das bereits
            // geholte Token bleibt für den zu startenden Prozess gültig.
            if (addedToGroup)
            {
                RemoveFromAdministrators(user.Sid);
                addedToGroup = false;
                onStep("Benutzer wieder aus der Administratorengruppe entfernt.");
            }

            var pid = StartProcessWithToken(linked.Token, exePath, arguments, workingDir);
            return new(true, pid, "Prozess erhöht gestartet.");
        }
        catch (Exception ex)
        {
            return new(false, 0, ex.Message);
        }
        finally
        {
            if (addedToGroup && samAccount is not null)
            {
                try { RemoveFromAdministratorsByAccount(samAccount); } catch { }
            }
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Startet ein Programm NICHT erhöht in der interaktiven Sitzung des angemeldeten
    /// Benutzers (z. B. die Tray-GUI nach einem Dienst-/Update-Neustart).
    /// </summary>
    public LaunchResult LaunchInUserSession(string exePath, string? arguments, Action<string> onStep)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            return new(false, 0, "Keine aktive Benutzersitzung.");

        EnablePrivilege(SE_TCB_NAME, onStep);
        if (!WTSQueryUserToken(sessionId, out var userToken))
            return new(false, 0, $"WTSQueryUserToken fehlgeschlagen: {Marshal.GetLastPInvokeError()}");

        try
        {
            // Benutzer-Token zu einem Primär-Token duplizieren (ohne Erhöhung).
            const uint access = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY
                                | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(userToken, access, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenPrimary, out var primary))
                return new(false, 0, $"DuplicateTokenEx fehlgeschlagen: {Marshal.GetLastPInvokeError()}");
            try
            {
                var pid = StartProcessWithToken(primary, exePath, arguments, null);
                return new(true, pid, "In Benutzersitzung gestartet.");
            }
            finally { CloseHandle(primary); }
        }
        catch (Exception ex) { return new(false, 0, ex.Message); }
        finally { CloseHandle(userToken); }
    }

    /// <summary>Aktiviert ein Privileg im Token des aktuellen (SYSTEM-)Prozesses.</summary>
    private static void EnablePrivilege(string privilege, Action<string> onStep)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            onStep($"OpenProcessToken fehlgeschlagen: {Marshal.GetLastPInvokeError()}");
            return;
        }
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                onStep($"LookupPrivilegeValue({privilege}) fehlgeschlagen: {Marshal.GetLastPInvokeError()}");
                return;
            }
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                onStep($"AdjustTokenPrivileges({privilege}) fehlgeschlagen: {Marshal.GetLastPInvokeError()}");
            else
                onStep($"Privileg {privilege} aktiviert.");
        }
        finally { CloseHandle(token); }
    }

    private readonly record struct ResolvedUser(SecurityIdentifier Sid, string Account, string Full);

    private static ResolvedUser ResolveUser(IntPtr userToken)
    {
        using var id = new WindowsIdentity(userToken);
        var sid = id.User ?? throw new InvalidOperationException("Token ohne Benutzer-SID.");
        var full = id.Name; // DOMAIN\user oder PC\user
        var account = full.Contains('\\') ? full[(full.IndexOf('\\') + 1)..] : full;
        return new(sid, account, full);
    }

    /// <summary>Holt das verknüpfte, nicht gefilterte Token (mit Admin-Rechten).</summary>
    private static SafeToken GetLinkedElevatedToken(IntPtr userToken)
    {
        // Erst ein abfragbares Duplikat des Benutzer-Tokens erstellen.
        if (!DuplicateTokenEx(userToken, TOKEN_DUPLICATE | TOKEN_QUERY, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenImpersonation, out var dup))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx (Abfrage) fehlgeschlagen.");

        try
        {
            var size = Marshal.SizeOf<TOKEN_LINKED_TOKEN>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(dup, TokenLinkedToken, buf, size, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        "TokenLinkedToken nicht lesbar (hat der Benutzer kein verknüpftes Admin-Token?).");

                var linked = Marshal.PtrToStructure<TOKEN_LINKED_TOKEN>(buf).LinkedToken;
                try
                {
                    // Verknüpftes Token zu einem Primär-Token für CreateProcessAsUser machen.
                    const uint access = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY
                                        | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
                    if (!DuplicateTokenEx(linked, access, IntPtr.Zero,
                            SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenPrimary, out var primary))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(),
                            "DuplicateTokenEx (Primär) fehlgeschlagen.");
                    return new SafeToken(primary);
                }
                finally { CloseHandle(linked); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(dup); }
    }

    private static int StartProcessWithToken(IntPtr primaryToken, string exePath, string? arguments,
        string? workingDir)
    {
        if (!CreateEnvironmentBlock(out var env, primaryToken, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEnvironmentBlock fehlgeschlagen.");
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            var dir = workingDir ?? Path.GetDirectoryName(exePath);

            if (!CreateProcessAsUser(primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS | CREATE_NEW_CONSOLE,
                    env, dir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser fehlgeschlagen.");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally { DestroyEnvironmentBlock(env); }
    }

    /// <summary>SID des Benutzers der aktiven Konsolensitzung (für Zeit-Admin).</summary>
    public static (SecurityIdentifier Sid, string Account, string Full)? GetActiveSessionUser()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return null;
        if (!WTSQueryUserToken(sessionId, out var token)) return null;
        try
        {
            var u = ResolveUser(token);
            return (u.Sid, u.Account, u.Full);
        }
        finally { CloseHandle(token); }
    }

    public static bool AddUserToAdministrators(SecurityIdentifier sid, out string detail)
        => AddToAdministrators(sid, out detail);

    public static void RemoveUserFromAdministrators(SecurityIdentifier sid)
        => RemoveFromAdministrators(sid);

    // --- Gruppenverwaltung über System.DirectoryServices.AccountManagement ---

    private static bool AddToAdministrators(SecurityIdentifier sid, out string detail)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, adminSid.Value)
            ?? throw new InvalidOperationException("Administratorengruppe nicht gefunden.");

        if (admins.Members.Any(m => m.Sid == sid))
        {
            detail = "Benutzer ist bereits Administrator — keine Änderung.";
            return false;
        }
        admins.Members.Add(ctx, IdentityType.Sid, sid.Value);
        admins.Save();
        detail = "Benutzer kurzzeitig in die Administratorengruppe aufgenommen.";
        return true;
    }

    private static void RemoveFromAdministrators(SecurityIdentifier sid)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, adminSid.Value);
        if (admins is null) return;
        var member = admins.Members.FirstOrDefault(m => m.Sid == sid);
        if (member is not null)
        {
            admins.Members.Remove(member);
            admins.Save();
        }
    }

    private static void RemoveFromAdministratorsByAccount(string account)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, adminSid.Value);
        if (admins is null) return;
        var member = admins.Members.FirstOrDefault(m =>
            string.Equals(m.SamAccountName, account, StringComparison.OrdinalIgnoreCase));
        if (member is not null)
        {
            admins.Members.Remove(member);
            admins.Save();
        }
    }

    private sealed class SafeToken(IntPtr token) : IDisposable
    {
        public IntPtr Token { get; } = token;
        public void Dispose() { if (Token != IntPtr.Zero) CloseHandle(Token); }
    }
}

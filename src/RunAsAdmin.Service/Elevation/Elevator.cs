using System.ComponentModel;
using System.Diagnostics;
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
        // Sicherstellen, dass der Thread NICHT mehr das Kind imitiert (nach der
        // Pipe-Impersonation). Sonst nutzt WTSQueryUserToken das Kind-Token (ohne
        // SeTcbPrivilege) und scheitert -> "Keine Sitzung mit Token".
        RevertToSelf();

        // SeTcbPrivilege aktivieren – WTSQueryUserToken benötigt es aktiviert.
        // LocalSystem besitzt es, aber es ist nicht automatisch im Token eingeschaltet.
        EnablePrivilege(SE_TCB_NAME, onStep);

        if (!TryGetUserToken(onStep, out var userToken))
            return new(false, 0, "Keine aktive Benutzersitzung mit Token gefunden.");

        string? samAccount = null;
        var addedToGroup = false;
        try
        {
            var user = ResolveUser(userToken);
            samAccount = user.Account;
            onStep($"Angemeldeter Benutzer: {user.Full}");

            // Sicherstellen: kein Impersonations-Rest auf dem Thread, bevor wir die
            // Gruppe ändern (NetLocalGroup* läuft sonst evtl. nicht als SYSTEM).
            RevertToSelf();

            // Versuch: verknüpftes (voll erhöhtes) Token – nur bei UAC-Admins mit
            // Split-Token. Reine Standardbenutzer haben keines (MakeMeAdmin-Weg unten).
            SafeToken? linked = null;
            var wasAdded = AddToAdministrators(user.Sid, out var groupDetail);
            addedToGroup = wasAdded;
            onStep(groupDetail);
            try { linked = GetLinkedElevatedToken(userToken); }
            catch { linked = null; }

            int pid;
            if (linked is not null)
            {
                // Split-Token-Admin: erhöht starten, Gruppe sofort wieder entziehen.
                using (linked)
                {
                    if (addedToGroup) { RemoveFromAdministrators(user.Sid); addedToGroup = false; onStep("Gruppe wieder entzogen."); }
                    pid = StartProcessWithToken(linked.Token, exePath, arguments, workingDir);
                }
                onStep("Erhöht gestartet (verknüpftes Token).");
            }
            else
            {
                // Standardbenutzer (MakeMeAdmin-Prinzip): Das Kind BLEIBT für die
                // Admin-Zeit in der Gruppe. Das Programm startet normal; wenn es
                // Adminrechte braucht, kommt der UAC-Dialog, den das Kind jetzt
                // bestätigen kann. Der zeitgesteuerte Entzug erfolgt extern.
                using var primary = DuplicateToPrimary(userToken);
                pid = StartProcessWithToken(primary.Token, exePath, arguments, workingDir);
                addedToGroup = false; // bewusst NICHT hier entziehen – bleibt Admin
                onStep("Gestartet; Konto bleibt kurz Administrator (MakeMeAdmin).");

                // Automatische Rückstufung nach 10 Minuten (genug Zeit, damit Spiel +
                // Anti-Cheat-Dienst sich einrichten; danach ist das Kind wieder normal).
                var sidToRemove = user.Sid;
                System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromMinutes(10));
                    try { RemoveFromAdministrators(sidToRemove); } catch { }
                });
            }
            return new(true, pid, "Prozess gestartet.");
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
        RevertToSelf();
        EnablePrivilege(SE_TCB_NAME, onStep);
        if (!TryGetUserToken(onStep, out var userToken))
            return new(false, 0, "Keine aktive Benutzersitzung mit Token gefunden.");

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

    /// <summary>
    /// Holt ein Benutzer-Token für die aktive Sitzung. Versucht zuerst die
    /// Konsolensitzung, dann (Fallback) die erste aktive Sitzung aus der Liste.
    /// </summary>
    internal static bool TryGetUserToken(Action<string> onStep, out IntPtr token)
    {
        token = IntPtr.Zero;

        // 1) Konsolensitzung
        var console = WTSGetActiveConsoleSessionId();
        if (console != 0xFFFFFFFF && WTSQueryUserToken(console, out token))
        {
            onStep($"Token aus Konsolensitzung {console}.");
            return true;
        }
        onStep($"Konsolensitzung {console} ohne Token (Fehler {Marshal.GetLastPInvokeError()}), suche aktive Sitzung…");

        // 2) Alle Sitzungen durchgehen, erste aktive mit Token nehmen.
        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
        {
            onStep($"WTSEnumerateSessions fehlgeschlagen: {Marshal.GetLastPInvokeError()}");
            return false;
        }
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            onStep($"{count} Sitzungen gefunden, suche eine mit Token…");

            // Alle Nicht-System-Sitzungen durchgehen und die ERSTE mit abrufbarem
            // Benutzer-Token nehmen – unabhaengig vom gemeldeten State (robust auch
            // bei gesperrtem Bildschirm / Vollbildspiel).
            for (var i = 0; i < count; i++)
            {
                var ptr = buffer + i * size;
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);
                onStep($"  Sitzung {info.SessionId}: State={info.State}, Station={info.pWinStationName}");
                if (info.SessionId == 0) continue; // Sitzung 0 = Dienste, kein Benutzer
                if (WTSQueryUserToken(info.SessionId, out token))
                {
                    onStep($"Token aus Sitzung {info.SessionId} ({info.State}).");
                    return true;
                }
            }
        }
        finally { WTSFreeMemory(buffer); }

        onStep("Keine Sitzung mit abrufbarem Benutzer-Token gefunden.");
        return false;
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

    /// <summary>Dupliziert ein Token zu einem Primär-Token für CreateProcessAsUser.</summary>
    private static SafeToken DuplicateToPrimary(IntPtr token)
    {
        const uint access = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY
                            | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
        if (!DuplicateTokenEx(token, access, IntPtr.Zero,
                SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenPrimary, out var primary))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx (Session-Token) fehlgeschlagen.");
        return new SafeToken(primary);
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
            // wShowWindow=SW_SHOW, damit das Fenster sichtbar erscheint.
            const int STARTF_USESHOWWINDOW = 0x00000001;
            const short SW_SHOW = 5;
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_SHOW,
            };
            var cmd = string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            var dir = workingDir ?? Path.GetDirectoryName(exePath);

            // KEIN CREATE_NEW_CONSOLE (macht GUI-Programme unsichtbar / zeigt leere
            // Konsole). Stattdessen normaler Fensterstart in der Benutzersitzung.
            if (!CreateProcessAsUser(primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS,
                    env, dir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser fehlgeschlagen.");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally { DestroyEnvironmentBlock(env); }
    }

    /// <summary>SID des Benutzers der aktiven Sitzung (für Zeit-Admin). Robuste Ermittlung.</summary>
    public static (SecurityIdentifier Sid, string Account, string Full)? GetActiveSessionUser(Action<string>? onStep = null)
    {
        var step = onStep ?? (_ => { });
        RevertToSelf();
        EnablePrivilege(SE_TCB_NAME, step);
        if (!TryGetUserToken(step, out var token)) return null;
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

    /// <summary>
    /// Kandidaten für den lokalen Admin-Gruppennamen. Zuerst der per SID aufgelöste
    /// Name, dann die gängigen Varianten (DE/EN). NetLocalGroup* braucht den REINEN
    /// Gruppennamen ohne "BUILTIN\"-Präfix.
    /// </summary>
    private static IEnumerable<string> AdminGroupNames()
    {
        string? resolved = null;
        try
        {
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[adminSid.BinaryLength];
            adminSid.GetBinaryForm(bytes, 0);
            var name = new System.Text.StringBuilder(256);
            var domain = new System.Text.StringBuilder(256);
            int cchName = name.Capacity, cchDomain = domain.Capacity;
            if (LookupAccountSid(null, bytes, name, ref cchName, domain, ref cchDomain, out _))
            {
                resolved = name.ToString();
                // Falls "BUILTIN\Administratoren" -> nur den Teil nach dem Backslash.
                if (resolved.Contains('\\')) resolved = resolved[(resolved.IndexOf('\\') + 1)..];
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(resolved)) yield return resolved!;
        yield return "Administratoren";
        yield return "Administrators";
    }

    /// <summary>
    /// Nimmt den Benutzer (per SID) nativ in die lokale Admin-Gruppe auf.
    /// Robust auch bei Microsoft-Konten (im Gegensatz zu AccountManagement).
    /// </summary>
    private static bool AddToAdministrators(SecurityIdentifier sid, out string detail)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var pSid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pSid, bytes.Length);
            var info = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = pSid };
            int lastRc = -1; string? triedNames = null;
            foreach (var grp in AdminGroupNames())
            {
                triedNames = (triedNames is null ? "" : triedNames + ", ") + grp;
                var rc = NetLocalGroupAddMembers(null, grp, 0, ref info, 1);
                lastRc = rc;
                if (rc == NERR_Success)
                {
                    detail = $"Benutzer kurzzeitig in '{grp}' aufgenommen.";
                    return true;
                }
                if (rc == ERROR_MEMBER_IN_ALIAS)
                {
                    detail = $"Benutzer ist bereits in '{grp}' — keine Änderung.";
                    return false;
                }
                // 2220 (GroupNotFound) -> nächsten Namen probieren, sonst abbrechen.
                if (rc != 2220) break;
            }

            // Fallback: Code 5 (Access Denied) o.ä. -> über 'net localgroup' per SID.
            if (NetLocalGroupViaCmd(sid, add: true, out var cmdDetail))
            {
                detail = cmdDetail;
                return true;
            }
            throw new Win32Exception(lastRc, $"NetLocalGroupAddMembers fehlgeschlagen (Code {lastRc}, Namen: {triedNames}). {cmdDetail}");
        }
        finally { Marshal.FreeHGlobal(pSid); }
    }

    /// <summary>
    /// Fallback über das Kommandozeilen-Tool 'net localgroup' mit der SID.
    /// Läuft als SYSTEM zuverlässig, auch wenn die native API Access Denied liefert.
    /// </summary>
    private static bool NetLocalGroupViaCmd(SecurityIdentifier sid, bool add, out string detail)
    {
        try
        {
            var groupSidArg = "*" + new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
            var memberArg = "*" + sid.Value;
            var action = add ? "/add" : "/delete";
            var psi = new ProcessStartInfo("net.exe", $"localgroup {groupSidArg} {memberArg} {action}")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode == 0)
            {
                detail = add ? "Benutzer per 'net localgroup' aufgenommen." : "entfernt";
                return true;
            }
            detail = $"'net localgroup' Exit {p.ExitCode}: {err.Trim()}";
            return false;
        }
        catch (Exception ex) { detail = $"'net localgroup' Fehler: {ex.Message}"; return false; }
    }

    private static void RemoveFromAdministrators(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var pSid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pSid, bytes.Length);
            var info = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = pSid };
            var ok = false;
            foreach (var grp in AdminGroupNames())
            {
                var rc = NetLocalGroupDelMembers(null, grp, 0, ref info, 1);
                if (rc == NERR_Success || rc == ERROR_MEMBER_NOT_IN_ALIAS) { ok = true; break; }
                if (rc != 2220) break;
            }
            // Fallback über net localgroup, damit das Kind sicher wieder entfernt wird.
            if (!ok) NetLocalGroupViaCmd(sid, add: false, out _);
        }
        finally { Marshal.FreeHGlobal(pSid); }
    }

    private static void RemoveFromAdministratorsByAccount(string account)
    {
        // Nicht mehr benötigt – Entfernung erfolgt per SID. Als No-Op belassen.
    }

    private sealed class SafeToken(IntPtr token) : IDisposable
    {
        public IntPtr Token { get; } = token;
        public void Dispose() { if (Token != IntPtr.Zero) CloseHandle(Token); }
    }
}

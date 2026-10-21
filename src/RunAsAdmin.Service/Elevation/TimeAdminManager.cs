using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using RunAsAdmin.Service.Elevation;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Elevation;

/// <summary>
/// Verwaltet eine zeitlich befristete, volle Admin-Sitzung für den angemeldeten
/// Benutzer. Immer nur EINE aktive Sitzung gleichzeitig. Nach Ablauf werden die
/// Adminrechte automatisch entzogen. Während der Sitzung werden Prozessstarts
/// protokolliert.
///
/// Sicherheit: Diese Funktion darf laut Vorgabe nur nach ausdrücklicher Freigabe
/// (Home Assistant) ausgelöst werden – das stellt der Aufrufer sicher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TimeAdminManager : IDisposable
{
    private readonly AuditLog _log;
    private readonly object _gate = new();

    private SecurityIdentifier? _activeSid;
    private string? _activeAccount;
    private DateTimeOffset _expiresAt;
    private Timer? _timer;
    private ManagementEventWatcher? _procWatcher;
    private int _processCount;
    private readonly List<string> _elevatedNames = new();

    // --- Fenster-Minimierung (Feature 1): sobald die freigegebene App laeuft, nach
    //     kurzer Karenz die Rechte wieder entziehen, statt die vollen Minuten offen
    //     zu lassen. _expectedAppExe = Dateiname der App, auf deren Start wir warten.
    private string? _expectedAppExe;
    private Timer? _earlyRevokeTimer;
    private bool _earlyRevokeArmed;
    private const int EarlyRevokeGraceSeconds = 15;

    // --- Missbrauchs-Erkennung (Feature 2): verdaechtige erhoehte Prozesse, die das
    //     Kind NICHT ueber eine freigegebene App startet, sondern mit den kurz
    //     gewaehrten Adminrechten selbst (regedit, cmd, Rechte-/Konten-Tools ...).
    private static readonly HashSet<string> SuspiciousExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "regedit.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "powershell_ise.exe",
        "mmc.exe", "taskmgr.exe", "net.exe", "net1.exe", "netplwiz.exe",
        "lusrmgr.msc", "secpol.msc", "gpedit.msc", "services.msc", "compmgmt.msc",
        "control.exe", "wscript.exe", "cscript.exe", "reg.exe", "sc.exe",
        "bcdedit.exe", "cmdkey.exe", "runas.exe", "psexec.exe", "psexec64.exe",
    };

    /// <summary>
    /// Wird gefeuert, wenn waehrend einer kurz-Admin-Zeit ein VERDAECHTIGER erhoehter
    /// Prozess erkannt wird (Kind nutzt die Rechte fuer etwas anderes als die App).
    /// Nur MELDEN – der Entzug laeuft normal weiter (bewusste Entscheidung).
    /// Argumente: (prozessName, pid).
    /// </summary>
    public Action<string, int>? OnSuspiciousActivity { get; set; }

    // Persistenter Zustand, damit eine Admin-Zeit einen Dienst-Neustart überlebt
    // bzw. nach einem Absturz sicher beendet werden kann (kein Dauer-Admin).
    private static readonly string StateFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RunAsAdmin", "timeadmin.state");

    public TimeAdminManager(AuditLog log)
    {
        _log = log;
        RecoverOnStartup();
    }

    /// <summary>
    /// Beim Dienststart: Falls eine frühere Admin-Zeit nicht sauber beendet wurde,
    /// wird sie je nach Restzeit fortgesetzt ODER das Konto wird sofort zurückgestuft.
    /// Verhindert, dass ein Kind durch Dienst-Kill dauerhaft Admin bleibt.
    /// </summary>
    private void RecoverOnStartup()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var parts = File.ReadAllText(StateFile).Split('|');
            if (parts.Length < 2) { TryDeleteState(); return; }
            var sid = new SecurityIdentifier(parts[0]);
            var expires = DateTimeOffset.Parse(parts[1]);

            if (expires <= DateTimeOffset.Now.AddSeconds(5))
            {
                // Abgelaufen (oder fast) -> sofort entziehen.
                Elevator.RemoveUserFromAdministrators(sid);
                _log.Write("timeadmin.recovered_revoke", new { sid = sid.Value, expires });
                TryDeleteState();
            }
            else
            {
                // Noch gültig -> Zeit fortsetzen, Timer neu setzen.
                lock (_gate)
                {
                    _activeSid = sid;
                    _activeAccount = TryResolveAccount(sid);
                    _expiresAt = expires;
                    _timer = new Timer(_ => Revoke("timeout"), null,
                        expires - DateTimeOffset.Now, Timeout.InfiniteTimeSpan);
                }
                _log.Write("timeadmin.recovered_resume", new { sid = sid.Value, expires });
            }
        }
        catch (Exception ex) { _log.Write("timeadmin.recover_failed", new { error = ex.Message }); TryDeleteState(); }
    }

    private static string? TryResolveAccount(SecurityIdentifier sid)
    {
        try { return (sid.Translate(typeof(NTAccount)) as NTAccount)?.Value; } catch { return null; }
    }

    private void WriteState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, $"{_activeSid?.Value}|{_expiresAt:o}");
        }
        catch { }
    }

    private static void TryDeleteState() { try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { } }

    public bool IsActive
    {
        get { lock (_gate) return _activeSid is not null; }
    }

    public int RemainingSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_activeSid is null) return 0;
                var s = (int)(_expiresAt - DateTimeOffset.Now).TotalSeconds;
                return s < 0 ? 0 : s;
            }
        }
    }

    /// <summary>
    /// Gewährt dem Benutzer der aktiven Sitzung für <paramref name="minutes"/>
    /// Minuten volle Adminrechte. Gibt (ok, Nachricht) zurück.
    /// </summary>
    public (bool Ok, string Message) Grant(int minutes, string requestId, string? reason)
    {
        lock (_gate)
        {
            if (_activeSid is not null)
                return (false, $"Es läuft bereits eine Admin-Zeit (noch {RemainingSeconds / 60} Min).");

            _log.Write("timeadmin.grant_begin", new { requestId });
            var user = Elevator.GetActiveSessionUser(s => _log.Write("timeadmin.session_step", new { requestId, step = s }));
            if (user is null)
            {
                _log.Write("timeadmin.no_session", new { requestId });
                return (false, "Keine aktive Benutzersitzung gefunden.");
            }
            _log.Write("timeadmin.session_ok", new { requestId, user = user.Value.Full });

            if (!Elevator.AddUserToAdministrators(user.Value.Sid, out var detail))
            {
                _log.Write("timeadmin.already_admin", new { requestId });
                return (false, "Benutzer ist bereits Administrator – keine Zeit-Sitzung nötig.");
            }

            _activeSid = user.Value.Sid;
            _activeAccount = user.Value.Account;
            _expiresAt = DateTimeOffset.Now.AddMinutes(minutes);
            _processCount = 0;
            _elevatedNames.Clear();
            WriteState();   // persistieren, damit ein Neustart die Zeit sauber beendet

            _log.Write("timeadmin.granted", new
            {
                requestId, user = user.Value.Full, minutes, reason, detail,
            });

            StartProcessWatch(user.Value.Account);
            _timer = new Timer(_ => Revoke("timeout"), null,
                TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            return (true, $"Du bist jetzt {minutes} Minuten lang Administrator.");
        }
    }

    /// <summary>
    /// Kurz-Admin für den Start einer FREIGEGEBENEN App (ohne Eltern-Freigabe, da
    /// die App vorab dauerhaft erlaubt wurde). Gewährt für <paramref name="minutes"/>
    /// Minuten Admin, damit die App erhöht im echten Kind-Kontext startet (sichtbar,
    /// richtiges Profil). Läuft schon eine Admin-Zeit, wird der Timer nur verlängert.
    /// Rückgabe: Ok = Konto ist jetzt (kurz) Admin, Programm kann gestartet werden.
    /// </summary>
    public (bool Ok, string Message) GrantForAppLaunch(int minutes, string appName)
        => GrantForAppLaunch(minutes, appName, null);

    /// <param name="appExePath">
    /// Pfad/Dateiname der App, auf deren erhöhten Start gewartet wird. Sobald dieser
    /// Prozess läuft, werden die Rechte nach kurzer Karenz vorzeitig entzogen
    /// (Fenster so kurz wie möglich). Null -> nur der feste Timer greift.
    /// </param>
    public (bool Ok, string Message) GrantForAppLaunch(int minutes, string appName, string? appExePath)
    {
        lock (_gate)
        {
            var exe = string.IsNullOrWhiteSpace(appExePath) ? null : Path.GetFileName(appExePath);

            // Bereits Admin-Zeit aktiv? -> nur verlängern, kein neuer Grant.
            if (_activeSid is not null)
            {
                var newExpiry = DateTimeOffset.Now.AddMinutes(minutes);
                if (newExpiry > _expiresAt)
                {
                    _expiresAt = newExpiry;
                    _timer?.Change(TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
                    WriteState();
                }
                // Neue App erwartet -> Früh-Entzug wieder scharf machen für diese App.
                if (exe is not null) { _expectedAppExe = exe; _earlyRevokeArmed = false; }
                _log.Write("applaunch.admin_extended", new { appName, minutes });
                return (true, "Adminrechte (kurz) verlängert.");
            }

            var user = Elevator.GetActiveSessionUser(s => _log.Write("applaunch.session_step", new { appName, step = s }));
            if (user is null)
                return (false, "Keine aktive Benutzersitzung gefunden.");

            // Schon dauerhaft Admin? Dann ist nichts zu tun – App startet ohnehin erhöht.
            if (!Elevator.AddUserToAdministrators(user.Value.Sid, out var detail))
            {
                _log.Write("applaunch.already_admin", new { appName });
                return (true, "Konto ist bereits Administrator.");
            }

            _activeSid = user.Value.Sid;
            _activeAccount = user.Value.Account;
            _expiresAt = DateTimeOffset.Now.AddMinutes(minutes);
            _processCount = 0;
            _elevatedNames.Clear();
            _expectedAppExe = exe;
            _earlyRevokeArmed = false;
            WriteState();

            _log.Write("applaunch.admin_granted", new { appName, minutes, user = user.Value.Full, detail, expectedExe = exe });
            StartProcessWatch(user.Value.Account);
            _timer = new Timer(_ => Revoke("applaunch_timeout"), null,
                TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            return (true, $"Kurz-Admin für {minutes} Minuten gewährt.");
        }
    }

    /// <summary>Entzieht die Adminrechte sofort (Timeout oder Dienst-Stopp).</summary>
    public void Revoke(string why)
    {
        lock (_gate)
        {
            if (_activeSid is null) return;
            try { Elevator.RemoveUserFromAdministrators(_activeSid); }
            catch (Exception ex) { _log.Write("timeadmin.revoke_error", new { error = ex.Message }); }

            StopProcessWatch();
            // Liste der Programme bereinigen (ohne .exe) für die HA-Zusammenfassung.
            var apps = _elevatedNames
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                .Distinct().ToArray();
            _log.Write("timeadmin.revoked", new
            {
                reason = why, account = _activeAccount,
                processesStarted = _processCount,
                apps,                               // Namen der erhöht gestarteten Programme
                appsText = string.Join(", ", apps), // kompakt für HA-Logbuch
            });

            _timer?.Dispose(); _timer = null;
            _earlyRevokeTimer?.Dispose(); _earlyRevokeTimer = null;
            _earlyRevokeArmed = false; _expectedAppExe = null;
            _activeSid = null; _activeAccount = null;
            TryDeleteState();
        }
    }

    private void StartProcessWatch(string account)
    {
        try
        {
            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _procWatcher = new ManagementEventWatcher(query);
            _procWatcher.EventArrived += (_, e) =>
            {
                try
                {
                    var name = e.NewEvent.Properties["ProcessName"]?.Value?.ToString() ?? "?";
                    var pidStr = e.NewEvent.Properties["ProcessID"]?.Value?.ToString() ?? "0";
                    if (!int.TryParse(pidStr, out var pid)) return;

                    // NUR Prozesse erfassen, die tatsächlich ERHÖHT (Admin) laufen –
                    // das sind die interessanten. Normale Prozesse ignorieren.
                    if (!Native.ProcessInfo.IsElevated(pid)) return;

                    bool isExpectedApp;
                    bool isSuspicious = SuspiciousExes.Contains(name);
                    lock (_gate)
                    {
                        _processCount++;
                        if (!_elevatedNames.Contains(name)) _elevatedNames.Add(name);
                        isExpectedApp = _expectedAppExe is not null &&
                            string.Equals(name, _expectedAppExe, StringComparison.OrdinalIgnoreCase);

                        // Feature 1: Die erwartete App läuft jetzt erhöht -> Fenster
                        // schließen. Noch kurze Karenz, damit Anti-Cheat-Dienst/Launcher
                        // -Kindprozesse sich einrichten können, dann sofort entziehen.
                        if (isExpectedApp && !_earlyRevokeArmed)
                        {
                            _earlyRevokeArmed = true;
                            _earlyRevokeTimer = new Timer(_ => Revoke("app_running_early"), null,
                                TimeSpan.FromSeconds(EarlyRevokeGraceSeconds), Timeout.InfiniteTimeSpan);
                            _log.Write("applaunch.app_detected", new
                            {
                                account, name, pid, revokeInSeconds = EarlyRevokeGraceSeconds,
                            });
                        }
                    }
                    _log.Write("timeadmin.process", new { account, name, pid, elevated = true, suspicious = isSuspicious });

                    // Feature 2: verdächtiger erhöhter Prozess (nicht die freigegebene
                    // App) -> nur MELDEN, Fenster läuft normal weiter.
                    if (isSuspicious && !isExpectedApp)
                    {
                        _log.Write("admin.suspicious", new { account, name, pid });
                        try { OnSuspiciousActivity?.Invoke(name, pid); } catch { }
                    }
                }
                catch { /* nie werfen */ }
            };
            _procWatcher.Start();
        }
        catch (Exception ex)
        {
            _log.Write("timeadmin.watch_failed", new { error = ex.Message });
        }
    }

    private void StopProcessWatch()
    {
        try { _procWatcher?.Stop(); _procWatcher?.Dispose(); }
        catch { }
        _procWatcher = null;
    }

    public void Dispose() => Revoke("service_stop");
}

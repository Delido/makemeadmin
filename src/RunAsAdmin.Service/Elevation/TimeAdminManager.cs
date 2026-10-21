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

                    lock (_gate)
                    {
                        _processCount++;
                        if (!_elevatedNames.Contains(name)) _elevatedNames.Add(name);
                    }
                    _log.Write("timeadmin.process", new { account, name, pid, elevated = true });
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

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

    public TimeAdminManager(AuditLog log) => _log = log;

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

            var user = Elevator.GetActiveSessionUser();
            if (user is null)
                return (false, "Keine aktive Benutzersitzung gefunden.");

            if (!Elevator.AddUserToAdministrators(user.Value.Sid, out var detail))
                return (false, "Benutzer ist bereits Administrator – keine Zeit-Sitzung nötig.");

            _activeSid = user.Value.Sid;
            _activeAccount = user.Value.Account;
            _expiresAt = DateTimeOffset.Now.AddMinutes(minutes);
            _processCount = 0;

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
            _log.Write("timeadmin.revoked", new
            {
                reason = why, account = _activeAccount, processesStarted = _processCount,
            });

            _timer?.Dispose(); _timer = null;
            _activeSid = null; _activeAccount = null;
        }
    }

    private void StartProcessWatch(string account)
    {
        try
        {
            // Alle Prozessstarts auf dem System; wir filtern grob und protokollieren.
            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            _procWatcher = new ManagementEventWatcher(query);
            _procWatcher.EventArrived += (_, e) =>
            {
                try
                {
                    var name = e.NewEvent.Properties["ProcessName"]?.Value?.ToString() ?? "?";
                    var pid = e.NewEvent.Properties["ProcessID"]?.Value?.ToString() ?? "?";
                    lock (_gate) _processCount++;
                    _log.Write("timeadmin.process", new { account, name, pid });
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

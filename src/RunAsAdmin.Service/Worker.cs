using System.Runtime.Versioning;
using RunAsAdmin.Service.Approval;
using RunAsAdmin.Service.Elevation;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service;

[SupportedOSPlatform("windows")]
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _configPath;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _configPath = config["ConfigPath"]
            ?? @"C:\ProgramData\RunAsAdmin\config.json";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppConfig cfg;
        try
        {
            cfg = AppConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Konfiguration konnte nicht geladen werden: {Path}", _configPath);
            return;
        }

        var log = new AuditLog(cfg.LogDirectory);

        // Ausgewählte Ereignisse nach außen melden (HA-Logbuch bzw. Add-on-Dashboard).
        using var reporter = new EventReporter(cfg);
        log.Sink = reporter.Report;

        log.Write("service.start", new { configPath = _configPath, apps = cfg.AllowedApps.Count });

        // Selbstheilung: sicherstellen, dass SYSTEM die eigene config.json schreiben darf.
        // Fruehere Installationen konnten eine zu strenge ACL hinterlassen -> dann
        // schlaegt "Immer erlauben"/Einstellungen-Speichern mit "Access denied" fehl.
        EnsureConfigWritable(_configPath, log);

        // Alte Protokolldateien aufräumen (älter als 90 Tage).
        TryRotateLogs(cfg.LogDirectory, keepDays: 90, log);

        // Freigabe-Weg je nach Modus: Server-Add-on (Polling) oder HA-Webhooks (Rückkanal).
        await using IApprover approver = cfg.Mode == ApprovalMode.Server
            ? new ServerApprover(cfg.Server, log)
            : new HomeAssistantApprover(cfg.HomeAssistant, log);
        try { approver.Start(); }
        catch (Exception ex) { log.Write("service.ha_listener_failed", new { error = ex.Message }); }
        log.Write("service.approval_mode", new { mode = cfg.Mode.ToString() });

        using var timeAdmin = new TimeAdminManager(log);
        approver.OnCancel = () => timeAdmin.Revoke("notaus");   // Not-Aus per Handy
        var handler = new RequestHandler(cfg, log, approver, timeAdmin);
        var pipe = new PipeServer(cfg, log, handler);

        // Server-Modus: App-Entfernungen vom Dashboard an den Handler durchreichen.
        if (approver is ServerApprover sa)
            sa.OnRemoveGames = names => handler.RemoveAppsByName(names);

        // Dem Server/HA die aktuell freigegebenen Spiele melden (Dashboard-Übersicht).
        handler.ReportRegisteredGames();

        // Tray-GUI in der Benutzersitzung sicherstellen (z. B. nach Update-Neustart).
        TryEnsureTrayGui(log);

        // Einmal prüfen, ob das Kind-Konto dauerhaft Admin ist (Tool sonst wirkungslos).
        try
        {
            var u = Elevation.Elevator.GetActiveSessionUser();
            if (u is not null && !timeAdmin.IsActive && Elevation.Elevator.IsPermanentAdmin(u.Value.Sid))
                log.Write("admin.permanent_warning", new { user = u.Value.Full });
        }
        catch { }

        _logger.LogInformation("RunAsAdmin-Dienst läuft. Pipe: {Pipe}", cfg.PipeName);
        try
        {
            await pipe.RunAsync(stoppingToken);
        }
        finally
        {
            log.Write("service.stop", new { });
        }
    }

    /// <summary>
    /// Stellt sicher, dass das SYSTEM-Konto die config.json schreiben darf. Testet
    /// mit einem Öffnen im Schreibmodus; schlägt das fehl, wird die ACL der Datei
    /// zurückgesetzt (SYSTEM + Administratoren Vollzugriff). Der Dienst läuft als
    /// SYSTEM und darf die ACL seiner eigenen Datei ändern.
    /// </summary>
    private static void EnsureConfigWritable(string configPath, AuditLog log)
    {
        try
        {
            if (!File.Exists(configPath)) return;
            // Schreibtest.
            try
            {
                using var fs = new FileStream(configPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return; // schreibbar – nichts zu tun
            }
            catch (UnauthorizedAccessException) { /* ACL reparieren */ }

            var fi = new FileInfo(configPath);
            var sec = new System.Security.AccessControl.FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                system, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                admins, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                users, System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
            log.Write("config.acl_repaired", new { configPath });
        }
        catch (Exception ex)
        {
            log.Write("config.acl_repair_failed", new { error = ex.Message });
        }
    }

    /// <summary>Löscht Audit-Dateien, die älter als keepDays sind.</summary>
    private static void TryRotateLogs(string dir, int keepDays, AuditLog log)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(dir, "audit-*.jsonl"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch (Exception ex) { log.Write("log.rotate_failed", new { error = ex.Message }); }
    }

    /// <summary>
    /// Startet die Tray-GUI in der aktiven Benutzersitzung, falls sie nicht läuft.
    /// So ist sie nach einem Dienst-/Update-Neustart automatisch wieder da.
    /// </summary>
    private static void TryEnsureTrayGui(AuditLog log)
    {
        try
        {
            // GUI liegt neben dem Dienst: ...\RunAsAdmin\Gui\RunAsAdmin.Gui.exe
            var serviceDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            var guiExe = Path.Combine(serviceDir, "Gui", "RunAsAdmin.Gui.exe");
            if (!File.Exists(guiExe)) { log.Write("gui.autostart_skip", new { reason = "exe fehlt", guiExe }); return; }

            // Evtl. hängende/alte GUI-Instanz beenden, dann frisch im Tray starten.
            // (Nach einem Update koennte eine alte GUI ohne sichtbares Tray laufen.)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("RunAsAdmin.Gui"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }

            // Kurz warten, bis die Benutzersitzung bereit ist.
            Thread.Sleep(3000);
            var r = new Elevation.Elevator().LaunchInUserSession(guiExe, "/tray",
                step => log.Write("gui.autostart_step", new { step }));
            log.Write("gui.autostart", new { r.Ok, r.Detail, r.ProcessId });
        }
        catch (Exception ex) { log.Write("gui.autostart_error", new { error = ex.Message }); }
    }
}

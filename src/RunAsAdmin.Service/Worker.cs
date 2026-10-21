using System.Runtime.Versioning;
using RunAsAdmin.Service.Approval;
using RunAsAdmin.Service.Elevation;
using RunAsAdmin.Service.Remote;
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

        // Ausgewählte Ereignisse zusätzlich an Home Assistant melden (Logbuch/Statistik).
        using var reporter = new EventReporter(cfg.HomeAssistant);
        log.Sink = reporter.Report;

        log.Write("service.start", new { configPath = _configPath, apps = cfg.AllowedApps.Count });

        // Alte Protokolldateien aufräumen (älter als 90 Tage).
        TryRotateLogs(cfg.LogDirectory, keepDays: 90, log);

        await using var approver = new HomeAssistantApprover(cfg.HomeAssistant, log);
        try { approver.Start(); }
        catch (Exception ex) { log.Write("service.ha_listener_failed", new { error = ex.Message }); }

        using var timeAdmin = new TimeAdminManager(log);
        approver.OnCancel = () => timeAdmin.Revoke("notaus");   // Not-Aus per Handy
        var handler = new RequestHandler(cfg, log, approver, timeAdmin);
        var pipe = new PipeServer(cfg, log, handler);

        using var remote = new RemoteTriggerServer(handler, log, cfg.HomeAssistant);
        try { remote.Start(); }
        catch (Exception ex) { log.Write("service.remote_listener_failed", new { error = ex.Message }); }

        // Tray-GUI in der Benutzersitzung sicherstellen (z. B. nach Update-Neustart).
        TryEnsureTrayGui(log);

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

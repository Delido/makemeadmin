using System.Diagnostics;
using System.Runtime.Versioning;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Installer;

[SupportedOSPlatform("windows")]
public sealed class Installer
{
    private readonly string _pkgRoot;
    private readonly TextWriter _out;

    private readonly string _programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private readonly string _programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private string InstallDir => Path.Combine(_programFiles, "RunAsAdmin");
    private string ClientDir => Path.Combine(InstallDir, "Client");
    private string GuiDir => Path.Combine(InstallDir, "Gui");
    private string DataDir => Path.Combine(_programData, "RunAsAdmin");
    private string LogDir => Path.Combine(DataDir, "logs");
    private string ConfigDest => Path.Combine(DataDir, "config.json");
    private string ServiceExe => Path.Combine(InstallDir, "RunAsAdmin.Service.exe");
    private string GuiExe => Path.Combine(GuiDir, "RunAsAdmin.Gui.exe");
    private const string ServiceName = "RunAsAdmin";

    /// <summary>Ordner mit den zu installierenden Dateien: payload\ bevorzugt, sonst publish\.</summary>
    private string PayloadDir
    {
        get
        {
            var payload = Path.Combine(_pkgRoot, "payload");
            return Directory.Exists(payload) ? payload : Path.Combine(_pkgRoot, "publish");
        }
    }

    public Installer(string pkgRoot, TextWriter outWriter)
    {
        _pkgRoot = pkgRoot;
        _out = outWriter;
    }

    public void Run()
    {
        CopyFiles();
        InstallConfig();
        FindProgramPaths();
        CreateEventSource();
        HardenPermissions();
        ReserveUrls();
        CreateShortcuts();
        CreateContextMenu();
        RegisterService();
    }

    private void Step(string msg) => _out.WriteLine($"  • {msg}");

    private void CopyFiles()
    {
        var pubService = Path.Combine(PayloadDir, "service");
        var pubClient = Path.Combine(PayloadDir, "client");
        var pubGui = Path.Combine(PayloadDir, "gui");
        if (!Directory.Exists(pubService))
            throw new DirectoryNotFoundException(
                $"Programmdateien nicht gefunden: {pubService}\nBitte den KOMPLETTEN Paketordner kopieren.");

        // Dienst bei laufender Installation ggf. stoppen (Update-Fall).
        TryStopService();

        CopyDir(pubService, InstallDir);
        if (Directory.Exists(pubClient)) CopyDir(pubClient, ClientDir);
        if (Directory.Exists(pubGui)) CopyDir(pubGui, GuiDir);
        Step("Programmdateien kopiert.");
    }

    private void InstallConfig()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);

        if (File.Exists(ConfigDest))
        {
            Step("Konfiguration bleibt erhalten (bereits vorhanden).");
            return;
        }
        // config liegt entweder in payload\config oder im Paketwurzel\config
        var src = Path.Combine(PayloadDir, "config", "config.json");
        if (!File.Exists(src)) src = Path.Combine(_pkgRoot, "config", "config.json");
        if (!File.Exists(src)) throw new FileNotFoundException($"config.json fehlt (gesucht in payload\\config und config).");
        // BOM-frei kopieren (robust fürs Laden im Dienst).
        var json = File.ReadAllText(src);
        File.WriteAllText(ConfigDest, json, new System.Text.UTF8Encoding(false));
        Step("Konfiguration installiert.");
    }

    private void FindProgramPaths()
    {
        try
        {
            var cfg = AppConfig.Load(ConfigDest);
            var changed = false;
            foreach (var app in cfg.AllowedApps)
            {
                if (!string.IsNullOrEmpty(app.Path) && File.Exists(app.Path)) continue;
                var found = ProgramFinder.Find(app.Name);
                if (found is not null) { app.Path = found; changed = true; Step($"gefunden: {app.Name}"); }
            }
            if (changed) cfg.Save(ConfigDest);
        }
        catch (Exception ex) { Step($"Pfadsuche übersprungen: {ex.Message}"); }
    }

    private void CreateEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(ServiceName))
                EventLog.CreateEventSource(ServiceName, "Application");
            Step("Ereignisquelle angelegt.");
        }
        catch (Exception ex) { Step($"Ereignisquelle übersprungen: {ex.Message}"); }
    }

    private void HardenPermissions()
    {
        // Vererbung aus, SYSTEM+Admins voll, Benutzer nur Lesen auf DataDir,
        // Logs komplett ohne Benutzerzugriff. Via icacls.
        Run("icacls", $"\"{DataDir}\" /inheritance:r");
        Run("icacls", $"\"{DataDir}\" /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-11:(OI)(CI)RX\"");
        Run("icacls", $"\"{LogDir}\" /inheritance:r");
        Run("icacls", $"\"{LogDir}\" /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\"");
        Step("Berechtigungen gehärtet (Kinder können Konfiguration/Protokolle nicht ändern).");
    }

    private void ReserveUrls()
    {
        try
        {
            var cfg = AppConfig.Load(ConfigDest);
            var p = cfg.HomeAssistant.CallbackPort;
            foreach (var (port, path) in new[] { (p, "decision"), (p + 1, "trigger") })
            {
                Run("netsh", $"http add urlacl url=http://+:{port}/{path}/ user=\"NT AUTHORITY\\SYSTEM\"", ignoreError: true);
                // Regel zuerst entfernen (falls mit altem Profil vorhanden), dann für ALLE Profile neu.
                Run("netsh", $"advfirewall firewall delete rule name=\"RunAsAdmin {path} ({port})\"", ignoreError: true);
                Run("netsh", $"advfirewall firewall add rule name=\"RunAsAdmin {path} ({port})\" dir=in action=allow protocol=TCP localport={port} profile=any", ignoreError: true);
            }
            Step("URL-Vorbehalte und Firewallregeln (alle Netzwerkprofile) gesetzt.");
        }
        catch (Exception ex) { Step($"URL-Vorbehalte übersprungen: {ex.Message}"); }
    }

    private void CreateShortcuts()
    {
        try
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            Shortcut.Create(Path.Combine(startMenu, "Programme starten (RunAsAdmin).lnk"), GuiExe, "", GuiExe);
            var startup = Path.Combine(startMenu, "StartUp");
            Shortcut.Create(Path.Combine(startup, "RunAsAdmin Tray.lnk"), GuiExe, "/tray", GuiExe);
            Step("Startmenü-Verknüpfung + Autostart (Tray) eingerichtet.");
        }
        catch (Exception ex) { Step($"Verknüpfungen übersprungen: {ex.Message}"); }
    }

    private void CreateContextMenu()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(@"exefile\shell\RunAsAdminRequest");
            key.SetValue("", "Mit Adminrechten anfragen");
            key.SetValue("Icon", $"{GuiExe},0");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{GuiExe}\" --request \"%1\"");
            Step("Rechtsklick-Menü 'Mit Adminrechten anfragen' eingerichtet.");
        }
        catch (Exception ex) { Step($"Rechtsklick-Menü übersprungen: {ex.Message}"); }
    }

    private void RegisterService()
    {
        TryStopService();
        Run("sc", $"delete {ServiceName}", ignoreError: true);
        System.Threading.Thread.Sleep(800);
        Run("sc", $"create {ServiceName} binPath= \"{ServiceExe}\" start= auto DisplayName= \"RunAsAdmin (kontrollierte Rechteerhoehung)\"");
        Run("sc", $"description {ServiceName} \"Startet freigegebene Programme fuer Standardbenutzer kontrolliert und protokolliert erhoeht.\"");
        Run("sc", $"start {ServiceName}");
        Step("Dienst installiert und gestartet.");
    }

    // --- Deinstallation ---

    public static void Uninstall(TextWriter o, bool removeLogs)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var installDir = Path.Combine(pf, "RunAsAdmin");
        var dataDir = Path.Combine(pd, "RunAsAdmin");

        RunStatic("sc", "stop RunAsAdmin", true);
        // Prozesse (Dienst + GUI) erzwungen beenden und auf Ende warten.
        for (var i = 0; i < 30; i++)
        {
            var procs = Process.GetProcessesByName("RunAsAdmin.Service")
                .Concat(Process.GetProcessesByName("RunAsAdmin.Gui")).ToArray();
            if (procs.Length == 0) break;
            foreach (var p in procs) { try { p.Kill(); p.WaitForExit(1000); } catch { } }
            Thread.Sleep(500);
        }
        RunStatic("sc", "delete RunAsAdmin", true);
        Thread.Sleep(800);

        var sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        TryDelete(Path.Combine(sm, "Programme starten (RunAsAdmin).lnk"));
        TryDelete(Path.Combine(sm, "StartUp", "RunAsAdmin Tray.lnk"));

        try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(@"exefile\shell\RunAsAdminRequest", false); } catch { }

        try
        {
            if (File.Exists(Path.Combine(dataDir, "config.json")))
            {
                var cfg = AppConfig.Load(Path.Combine(dataDir, "config.json"));
                var p = cfg.HomeAssistant.CallbackPort;
                RunStatic("netsh", $"http delete urlacl url=http://+:{p}/decision/", true);
                RunStatic("netsh", $"http delete urlacl url=http://+:{p + 1}/trigger/", true);
                RunStatic("netsh", $"advfirewall firewall delete rule name=\"RunAsAdmin decision ({p})\"", true);
                RunStatic("netsh", $"advfirewall firewall delete rule name=\"RunAsAdmin trigger ({p + 1})\"", true);
            }
        }
        catch { }

        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, true); } catch (Exception ex) { o.WriteLine($"  Hinweis: {ex.Message}"); }
        try { if (EventLog.SourceExists("RunAsAdmin")) EventLog.DeleteEventSource("RunAsAdmin"); } catch { }

        if (removeLogs) { try { Directory.Delete(dataDir, true); } catch { } o.WriteLine("  Protokolle entfernt."); }
        else o.WriteLine($"  Protokolle bleiben erhalten: {dataDir}");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void RunStatic(string file, string args, bool ignoreError)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
        catch { if (!ignoreError) throw; }
    }

    // --- Helfer ---

    private void TryStopService()
    {
        Run("sc", $"stop {ServiceName}", ignoreError: true);

        // GUI + Dienst-Prozess erzwungen beenden und auf echtes Ende warten –
        // sonst bleibt RunAsAdmin.Service.exe gesperrt und wird nicht ersetzt.
        for (var i = 0; i < 30; i++)
        {
            var procs = Process.GetProcessesByName("RunAsAdmin.Service")
                .Concat(Process.GetProcessesByName("RunAsAdmin.Gui")).ToArray();
            if (procs.Length == 0) break;
            foreach (var p in procs)
            {
                try { p.Kill(); p.WaitForExit(1000); } catch { }
            }
            System.Threading.Thread.Sleep(500);
        }
        System.Threading.Thread.Sleep(500);
    }

    private void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(src, dst);
            CopyFileRobust(file, target);
        }
    }

    /// <summary>
    /// Kopiert eine Datei und umgeht eine gesperrte Zieldatei: Die alte Datei wird
    /// umbenannt (das geht auch bei laufender Nutzung) und zum Löschen beim Neustart
    /// vorgemerkt, dann die neue an ihren Platz geschrieben.
    /// </summary>
    private void CopyFileRobust(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            try
            {
                var old = target + ".old-" + Guid.NewGuid().ToString("N")[..6];
                File.Move(target, old);
                try { MoveFileEx(old, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                File.Copy(source, target, overwrite: true);
                Step($"ersetzt (gesperrt, alte Datei beim Neustart entfernt): {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                Step($"WARNUNG: konnte nicht ersetzen: {Path.GetFileName(target)} ({ex.Message})");
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private void Run(string file, string args, bool ignoreError = false)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreError)
        {
            var err = p.StandardError.ReadToEnd().Trim();
            throw new Exception($"{file} {args} -> Exit {p.ExitCode} {err}");
        }
    }
}

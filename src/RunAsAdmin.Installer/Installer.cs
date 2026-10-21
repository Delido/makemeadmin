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
        CleanupUpdateTemp();   // alte, evtl. gesperrte Update-Reste entfernen
        ReserveUrls();
        CreateShortcuts();
        CreateContextMenu();
        RegisterService();
    }

    /// <summary>
    /// Räumt den Update-Download-Ordner des Dienstes auf. Ein früherer Lauf konnte
    /// dort eine gesperrte RunAsAdmin-Update.zip hinterlassen, die das Auto-Update
    /// blockiert ("Access denied"). Best-effort, inkl. Rechte-Übernahme.
    /// </summary>
    private void CleanupUpdateTemp()
    {
        try
        {
            // SYSTEM-Temp (Dienst läuft als SYSTEM): C:\Windows\SystemTemp bzw. ...\Temp
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp", "RunAsAdminUpdateSvc"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "RunAsAdminUpdateSvc"),
            };
            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir)) continue;
                Run("takeown", $"/f \"{dir}\" /r /d j", ignoreError: true);
                Run("icacls", $"\"{dir}\" /grant \"*S-1-5-18:(OI)(CI)F\" /t", ignoreError: true);
                try { Directory.Delete(dir, true); } catch { }
            }
            Step("Alte Update-Reste entfernt.");
        }
        catch (Exception ex) { Step($"Update-Reste übersprungen: {ex.Message}"); }
    }

    private void Step(string msg) => _out.WriteLine($"  • {msg}");

    private void CopyFiles()
    {
        var pubService = Path.Combine(PayloadDir, "service");
        var pubGui = Path.Combine(PayloadDir, "gui");
        if (!Directory.Exists(pubService))
            throw new DirectoryNotFoundException(
                $"Programmdateien nicht gefunden: {pubService}\nBitte den KOMPLETTEN Paketordner kopieren.");

        // Dienst bei laufender Installation ggf. stoppen (Update-Fall).
        TryStopService();

        CopyDir(pubService, InstallDir);
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

        // WICHTIG: config.json EXPLIZIT fuer SYSTEM+Admins beschreibbar machen.
        // Sonst kann der Dienst (SYSTEM) seine eigene Konfiguration nicht speichern
        // (z. B. "Immer erlauben"/Einstellungen) – fruehere Laeufe konnten hier eine
        // zu strenge ACL hinterlassen haben, die Vererbung allein nicht aufhebt.
        if (File.Exists(ConfigDest))
        {
            Run("icacls", $"\"{ConfigDest}\" /inheritance:r");
            Run("icacls", $"\"{ConfigDest}\" /grant:r \"*S-1-5-18:F\" \"*S-1-5-32-544:F\" \"*S-1-5-11:R\"");
        }
        Step("Berechtigungen gehärtet (Kinder können Konfiguration/Protokolle nicht ändern).");
    }

    private void ReserveUrls()
    {
        try
        {
            var cfg = AppConfig.Load(ConfigDest);
            // Nur der Home-Assistant-Modus braucht eingehende Ports (Rückkanal-Listener).
            // Der Server-Modus pollt rein ausgehend – kein offener Port nötig.
            if (cfg.Mode != ApprovalMode.HomeAssistant)
            {
                Step("Server-Modus: keine eingehenden Ports nötig.");
                return;
            }
            var p = cfg.HomeAssistant.CallbackPort;
            foreach (var (port, path) in new[] { (p, "decision"), (p, "cancel") })
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

        // WICHTIG: Bei einem Update existiert der Dienst noch. Ein "sc delete" markiert
        // ihn nur "zum Loeschen" (pending), solange noch Handles offen sind – ein
        // direkt folgendes "sc create" scheitert dann mit Exit 1073 ("Dienst existiert
        // bereits"), und es laeuft die ALTE Version weiter (bis zum Reboot).
        // Deshalb: existiert der Dienst -> NUR die binPath aktualisieren (sc config),
        // nicht loeschen+neu. Nur wenn er fehlt, neu anlegen.
        if (ServiceExists())
        {
            // Zuerst die zu strenge Haertung aufheben, sonst darf selbst der Admin-
            // Installer den Dienst nicht mehr konfigurieren (das war die Ursache fuer 1073).
            Run("sc", $"sdset {ServiceName} \"D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)(A;;CCLCSWLORC;;;SU)\"", ignoreError: true);
            Run("sc", $"config {ServiceName} binPath= \"{ServiceExe}\" start= auto", ignoreError: true);
            Step("Bestehenden Dienst aktualisiert (ohne Neuanlage).");
        }
        else
        {
            var createResult = RunGetExit("sc", $"create {ServiceName} binPath= \"{ServiceExe}\" start= auto DisplayName= \"RunAsAdmin (kontrollierte Rechteerhoehung)\"");
            if (createResult == 1073)
            {
                // Falls doch noch vorhanden: warten bis der pending-delete durch ist, dann config.
                for (var i = 0; i < 20 && ServiceExists(); i++) System.Threading.Thread.Sleep(500);
                Run("sc", $"config {ServiceName} binPath= \"{ServiceExe}\" start= auto", ignoreError: true);
            }
        }
        Run("sc", $"description {ServiceName} \"Startet freigegebene Programme fuer Standardbenutzer kontrolliert und protokolliert erhoeht.\"", ignoreError: true);

        // Härtung: Kinder (Standardbenutzer) duerfen den Dienst nicht stoppen/aendern.
        // ABER SYSTEM UND Administratoren behalten Vollzugriff – sonst kann weder der
        // Dienst sich selbst updaten noch ein kuenftiges Setup ihn verwalten (1073!).
        // Das Kind ist kein Admin, also bleibt der Schutz vor den Kindern erhalten.
        Run("sc", $"sdset {ServiceName} " +
            "\"D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)(A;;CCLCSWLORC;;;SU)\"",
            ignoreError: true);

        Run("sc", $"failure {ServiceName} reset= 60 actions= restart/5000/restart/5000/restart/5000", ignoreError: true);
        Run("sc", $"start {ServiceName}", ignoreError: true);

        // Warten, bis der Dienst wirklich RUNNING ist. Sonst kann eine sofort
        // geöffnete GUI auf einen noch startenden Dienst treffen -> Pipe-Timeout
        // ("operation timed out", Einstellungen scheinbar weg bis zum Neustart).
        for (var i = 0; i < 20; i++)
        {
            if (ServiceIsRunning()) break;
            System.Threading.Thread.Sleep(500);
        }
        Step("Dienst installiert, gehärtet (nicht durch Benutzer stoppbar) und gestartet.");
    }

    private static bool ServiceIsRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query RunAsAdmin")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return outp.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // --- Deinstallation ---

    public static void Uninstall(TextWriter o, bool removeLogs)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var installDir = Path.Combine(pf, "RunAsAdmin");
        var dataDir = Path.Combine(pd, "RunAsAdmin");

        // Dienst ist gegen Stoppen durch Admins gehärtet -> Stop+Delete als SYSTEM.
        RunAsSystem("sc.exe stop RunAsAdmin & timeout /t 3 /nobreak >nul & sc.exe delete RunAsAdmin", o);
        Thread.Sleep(2000);

        RunStatic("sc", "stop RunAsAdmin", true); // Fallback (falls schon entsperrt)
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
                RunStatic("netsh", $"http delete urlacl url=http://+:{p}/cancel/", true);
                RunStatic("netsh", $"advfirewall firewall delete rule name=\"RunAsAdmin decision ({p})\"", true);
                RunStatic("netsh", $"advfirewall firewall delete rule name=\"RunAsAdmin cancel ({p})\"", true);
            }
        }
        catch { }

        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, true); } catch (Exception ex) { o.WriteLine($"  Hinweis: {ex.Message}"); }
        try { if (EventLog.SourceExists("RunAsAdmin")) EventLog.DeleteEventSource("RunAsAdmin"); } catch { }

        if (removeLogs) { try { Directory.Delete(dataDir, true); } catch { } o.WriteLine("  Protokolle entfernt."); }
        else o.WriteLine($"  Protokolle bleiben erhalten: {dataDir}");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>
    /// Führt ein cmd-Kommando als SYSTEM aus (über eine kurzlebige geplante Aufgabe).
    /// Nötig, weil der Dienst gegen Stoppen durch Administratoren gehärtet ist.
    /// </summary>
    private static void RunAsSystem(string cmdLine, TextWriter o)
    {
        var task = "RunAsAdmin_SysOp";
        var bat = Path.Combine(Path.GetTempPath(), $"raa_sysop_{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(bat, "@echo off\r\n" + cmdLine + "\r\n");
            RunStatic("schtasks", $"/Create /TN {task} /TR \"cmd /c \\\"{bat}\\\"\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F", true);
            RunStatic("schtasks", $"/Run /TN {task}", true);
            Thread.Sleep(4000);
            RunStatic("schtasks", $"/Delete /TN {task} /F", true);
        }
        catch (Exception ex) { o.WriteLine($"  RunAsSystem: {ex.Message}"); }
        finally { try { File.Delete(bat); } catch { } }
    }

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

    private static bool ServiceExists()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query RunAsAdmin")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void TryStopService()
    {
        // Falls eine gehärtete Installation existiert: Stop als SYSTEM.
        if (ServiceExists())
        {
            RunAsSystem("sc.exe stop RunAsAdmin", _out);
            System.Threading.Thread.Sleep(1500);
        }
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

    /// <summary>Führt ein Kommando aus und gibt den Exit-Code zurück (ohne Exception).</summary>
    private int RunGetExit(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return -1; }
    }
}

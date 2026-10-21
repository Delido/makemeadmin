using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Elevation;

/// <summary>
/// Startet dauerhaft freigegebene Programme (z. B. Spiele mit Anti-Cheat) erhöht –
/// über eine geplante Aufgabe (Task Scheduler) statt über Token-Manipulation.
/// Die Aufgabe läuft als der angemeldete Benutzer mit „höchsten Rechten", also
/// erhöht und in dessen interaktiver Sitzung, aber OHNE UAC-Abfrage. Das Kind ist
/// dabei zu keinem Zeitpunkt selbst Administrator – nur dieses eine Programm läuft
/// erhöht.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskLauncher
{
    private readonly AuditLog _log;
    private const string TaskPrefix = "RunAsAdmin_Game_";

    public TaskLauncher(AuditLog log) => _log = log;

    /// <summary>Technischer Aufgabenname für ein freigegebenes Programm (stabil pro Pfad).</summary>
    public static string TaskNameFor(AllowedApp app)
    {
        // Stabil und kollisionsarm: Anzeigename (gesäubert) + kurzer Pfad-Hash.
        var safe = new string((app.Name ?? "spiel").Select(c =>
            char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safe.Length > 40) safe = safe[..40];
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(app.Path.ToLowerInvariant())))[..8];
        return $"{TaskPrefix}{safe}_{hash}";
    }

    /// <summary>
    /// Legt die geplante Aufgabe für ein Programm an (oder aktualisiert sie). Läuft
    /// als der angemeldete Benutzer mit höchsten Rechten. Gibt Erfolg + Meldung zurück.
    /// </summary>
    public (bool Ok, string Message) Register(AllowedApp app, string runAsAccount)
    {
        if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
            return (false, "Programmdatei nicht gefunden.");

        var taskName = TaskNameFor(app);
        var workDir = Path.GetDirectoryName(app.Path) ?? "";
        var xml = BuildTaskXml(app.Path, app.Arguments, workDir, runAsAccount);

        var xmlFile = Path.Combine(Path.GetTempPath(), taskName + ".xml");
        try
        {
            // Task Scheduler erwartet UTF-16 mit BOM.
            File.WriteAllText(xmlFile, xml, new UnicodeEncoding(false, true));

            // WICHTIG: KEIN /RU auf der Kommandozeile! Der Principal (UserId +
            // InteractiveToken + RunLevel) steht vollständig im XML. Mit /RU ohne /RP
            // fragt schtasks interaktiv nach einem Passwort und hängt dann endlos,
            // weil kein stdin vorhanden ist. Das XML genügt völlig.
            var (code, output) = RunSchtasks(
                $"/Create /TN \"{taskName}\" /XML \"{xmlFile}\" /F");
            if (code != 0)
            {
                _log.Write("game.task_register_failed", new { app.Name, taskName, code, output });
                return (false, $"Aufgabe konnte nicht angelegt werden (Code {code}).");
            }

            _log.Write("game.task_registered", new { app.Name, taskName, runAsAccount });
            return (true, "Spiel dauerhaft freigegeben.");
        }
        catch (Exception ex)
        {
            _log.Write("game.task_register_error", new { app.Name, error = ex.Message });
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlFile)) File.Delete(xmlFile); } catch { }
        }
    }

    /// <summary>Startet die geplante Aufgabe eines freigegebenen Programms.</summary>
    public (bool Ok, string Message) Launch(AllowedApp app)
    {
        var taskName = TaskNameFor(app);
        var (code, output) = RunSchtasks($"/Run /TN \"{taskName}\"");
        if (code != 0)
        {
            _log.Write("game.launch_failed", new { app.Name, taskName, code, output });
            return (false, $"Start fehlgeschlagen (Code {code}). Ist das Spiel noch freigegeben?");
        }
        _log.Write("game.launched", new { app.Name, taskName });
        return (true, $"{app.Name} wird gestartet…");
    }

    /// <summary>Entfernt die geplante Aufgabe (wenn ein Spiel die Freigabe verliert).</summary>
    public void Unregister(AllowedApp app)
    {
        var taskName = TaskNameFor(app);
        var (code, _) = RunSchtasks($"/Delete /TN \"{taskName}\" /F");
        _log.Write("game.task_unregistered", new { app.Name, taskName, code });
    }

    /// <summary>Baut die Aufgaben-XML: erhöht, interaktiv, im Spiel-Ordner.</summary>
    private static string BuildTaskXml(string exePath, string? args, string workDir, string account)
    {
        string E(string s) => SecurityElement.Escape(s) ?? "";
        var argLine = string.IsNullOrWhiteSpace(args) ? "" : $"      <Arguments>{E(args)}</Arguments>\n";
        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>RunAsAdmin: dauerhaft freigegebenes Programm (erhöht, ohne UAC).</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{E(account)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{E(exePath)}</Command>
{argLine}      <WorkingDirectory>{E(workDir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }

    private static (int Code, string Output) RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,    // stdin schließen -> keine interaktive Abfrage hängt
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p is null) return (-1, "schtasks nicht gestartet");

        // stdin sofort schließen: fragt schtasks doch nach etwas (z. B. Passwort),
        // bekommt es EOF und bricht ab, statt endlos zu warten.
        try { p.StandardInput.Close(); } catch { }

        // Ausgabe asynchron einsammeln, damit das Lesen nicht blockiert.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(true); } catch { }
            return (-2, "schtasks hängt (Timeout) – abgebrochen");
        }
        var outp = (outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult()).Trim();
        return (p.ExitCode, outp);
    }
}

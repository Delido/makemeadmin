using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service;

/// <summary>
/// Spielt ein heruntergeladenes Update-ZIP ein. Der Dienst läuft als SYSTEM und
/// darf daher nach Program Files schreiben. Da laufende exe-Dateien (Dienst, GUI)
/// nicht direkt überschrieben werden können, erledigt ein kurzlebiges Batch-Skript
/// das Ersetzen, nachdem die Prozesse beendet wurden, und startet den Dienst neu.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateApplier
{
    public static ElevationResponse Apply(string zipPath, AuditLog log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return Fail("Update-Datei nicht gefunden.");

            var installRoot = Path.GetDirectoryName(Environment.ProcessPath)!; // ...\Program Files\RunAsAdmin
            var staging = Path.Combine(Path.GetTempPath(), "RunAsAdminUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            log.Write("update.extracted", new { zipPath, staging });

            // Erwartete Struktur im ZIP: service\, client\, gui\ (wie im publish-Ordner).
            // Batch kopiert diese Unterordner an ihre Zielorte.
            var bat = Path.Combine(staging, "apply.cmd");
            var script = $"""
                @echo off
                rem RunAsAdmin Update-Anwendung (von SYSTEM gestartet)
                timeout /t 2 /nobreak >nul
                sc stop RunAsAdmin >nul 2>&1
                timeout /t 2 /nobreak >nul
                taskkill /im RunAsAdmin.Gui.exe /f >nul 2>&1
                if exist "{staging}\service" robocopy "{staging}\service" "{installRoot}" /E /NFL /NDL /NJH /NJS /NP >nul
                if exist "{staging}\client"  robocopy "{staging}\client"  "{installRoot}\Client" /E /NFL /NDL /NJH /NJS /NP >nul
                if exist "{staging}\gui"     robocopy "{staging}\gui"     "{installRoot}\Gui" /E /NFL /NDL /NJH /NJS /NP >nul
                sc start RunAsAdmin >nul 2>&1
                rmdir /s /q "{staging}" >nul 2>&1
                """;
            File.WriteAllText(bat, script);

            // Batch als eigenständigen Prozess starten (überlebt den Dienst-Neustart).
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = staging,
            });

            log.Write("update.applying", new { installRoot });
            return new ElevationResponse
            {
                State = RequestState.Approved,
                Decision = "Update",
                Message = "Update wird eingespielt. Der Dienst startet gleich neu.",
            };
        }
        catch (Exception ex)
        {
            log.Write("update.failed", new { error = ex.Message });
            return Fail($"Update fehlgeschlagen: {ex.Message}");
        }
    }

    private static ElevationResponse Fail(string msg) =>
        new() { State = RequestState.Error, Decision = "Update", Message = msg };
}

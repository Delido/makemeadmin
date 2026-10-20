using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using RunAsAdmin.Installer;

[assembly: SupportedOSPlatform("windows")]

Console.Title = "RunAsAdmin Setup";
Console.WriteLine("============================================");
Console.WriteLine("   RunAsAdmin – Installation");
Console.WriteLine("============================================\n");

try
{
    if (!IsAdmin())
    {
        Console.WriteLine("Es werden Administratorrechte benötigt.");
        Pause();
        return 1;
    }

    if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("RunAsAdmin wird entfernt...\n");
        Installer.Uninstall(Console.Out, removeLogs: args.Contains("--remove-logs"));
        Console.WriteLine("\nRunAsAdmin wurde entfernt.");
        Pause();
        return 0;
    }

    // Paketordner: Verzeichnis der Setup-exe. Erwartet Unterordner publish\ und config\.
    var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
    var pkgRoot = FindPackageRoot(exeDir);
    Console.WriteLine($"Paketordner: {pkgRoot}\n");

    var steps = new Installer(pkgRoot, Console.Out);
    steps.Run();

    Console.WriteLine("\nFERTIG. RunAsAdmin ist installiert.");
    Console.WriteLine("Nach dem nächsten Anmelden des Kindes erscheint das Tray-Symbol.");
    Pause();
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"\nFEHLER: {ex.Message}");
    Console.WriteLine("Bitte Meldung notieren und melden.");
    Pause();
    return 2;
}

static bool IsAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}

static string FindPackageRoot(string exeDir)
{
    // 1) exe liegt direkt im Paketwurzelordner (publish\ daneben)
    if (Directory.Exists(Path.Combine(exeDir, "publish"))) return exeDir;
    // 2) exe liegt in einem Unterordner (z. B. publish\installer) -> eine Ebene hoch
    var up = Directory.GetParent(exeDir)?.FullName;
    if (up is not null && Directory.Exists(Path.Combine(up, "publish"))) return up;
    var up2 = up is null ? null : Directory.GetParent(up)?.FullName;
    if (up2 is not null && Directory.Exists(Path.Combine(up2, "publish"))) return up2;
    // Fallback: exe-Ordner
    return exeDir;
}

static void Pause()
{
    Console.WriteLine("\nTaste drücken zum Schließen...");
    try { Console.ReadKey(); } catch { }
}

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

    // Paketordner: der Ordner, in dem die Setup-EXE wirklich liegt. Bei Single-File
    // ist AppContext.BaseDirectory ein Temp-Ordner – daher ProcessPath verwenden.
    var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
    var exeDir = (Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory).TrimEnd('\\');
    var pkgRoot = FindPackageRoot(exeDir);
    Console.WriteLine($"Setup-Ordner: {exeDir}");
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
    // Als Paket gilt ein Ordner, der "payload" ODER "publish" enthaelt.
    bool HasPayload(string d) => Directory.Exists(Path.Combine(d, "payload"))
                                 || Directory.Exists(Path.Combine(d, "publish"));

    if (HasPayload(exeDir)) return exeDir;
    var up = Directory.GetParent(exeDir)?.FullName;
    if (up is not null && HasPayload(up)) return up;
    var up2 = up is null ? null : Directory.GetParent(up)?.FullName;
    if (up2 is not null && HasPayload(up2)) return up2;
    return exeDir;
}

static void Pause()
{
    Console.WriteLine("\nTaste drücken zum Schließen...");
    try { Console.ReadKey(); } catch { }
}

namespace RunAsAdmin.Installer;

/// <summary>Sucht bekannte Programme an üblichen Installationsorten.</summary>
public static class ProgramFinder
{
    public static string? Find(string name)
    {
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidates = name switch
        {
            "UniGetUI" => new[]
            {
                Path.Combine(la, @"Programs\UniGetUI\UniGetUI.exe"),
                Path.Combine(la, @"Programs\WingetUI\WingetUI.exe"),
                Path.Combine(pf, @"UniGetUI\UniGetUI.exe"),
            },
            "SignalRGB" => new[]
            {
                @"C:\Users\Public\SignalRgb\current\SignalRgbLauncher.exe",
            },
            "Steam" => new[]
            {
                Path.Combine(pfx, @"Steam\steam.exe"),
                Path.Combine(pf, @"Steam\steam.exe"),
            },
            _ when name.StartsWith("Epic", StringComparison.OrdinalIgnoreCase) => new[]
            {
                Path.Combine(pfx, @"Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"),
                Path.Combine(pf, @"Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"),
            },
            _ => Array.Empty<string>(),
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // SignalRGB liegt manchmal in app-*-Ordnern
        if (name == "SignalRGB")
        {
            try
            {
                var baseDir = Path.Combine(la, "VortxEngine");
                if (Directory.Exists(baseDir))
                    foreach (var d in Directory.GetDirectories(baseDir, "app-*"))
                    {
                        var exe = Path.Combine(d, "SignalRgbLauncher.exe");
                        if (File.Exists(exe)) return exe;
                    }
            }
            catch { }
        }
        return null;
    }
}

using System.Runtime.Versioning;

namespace RunAsAdmin.Installer;

/// <summary>Erstellt .lnk-Verknüpfungen über die WScript.Shell-COM-Komponente.</summary>
[SupportedOSPlatform("windows")]
public static class Shortcut
{
    public static void Create(string lnkPath, string target, string arguments, string iconPath)
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell nicht verfügbar.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = target;
            sc.Arguments = arguments;
            sc.IconLocation = $"{iconPath},0";
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}

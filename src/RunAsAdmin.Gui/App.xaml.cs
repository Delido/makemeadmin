using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace RunAsAdmin.Gui;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Rechtsklick-Modus: "RunAsAdmin.Gui.exe --request <pfad>" -> nur den
        // Anfrage-Dialog zeigen, kein Tray, kein Hauptfenster.
        var reqIdx = Array.FindIndex(e.Args, a =>
            a.Equals("--request", StringComparison.OrdinalIgnoreCase));
        if (reqIdx >= 0 && reqIdx + 1 < e.Args.Length)
        {
            var path = e.Args[reqIdx + 1];
            var dlg = new RequestWindow(path);
            dlg.Closed += (_, _) => Shutdown();
            dlg.Show();
            return;
        }

        _window = new MainWindow();
        _window.Closing += Window_Closing;

        _tray = new WinForms.NotifyIcon
        {
            Text = "RunAsAdmin – Programme starten",
            Icon = LoadIcon(),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Programme starten…", null, (_, _) => ShowWindow());
        menu.Items.Add("Einstellungen (Eltern)…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;

        // Beim Start: nur ins Tray, wenn mit /tray gestartet (z. B. Autostart),
        // sonst Fenster direkt zeigen.
        if (!e.Args.Contains("/tray"))
            ShowWindow();
    }

    private void ShowSettings()
    {
        var w = new SettingsWindow();
        w.Show();
        w.Activate();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // Schließen (X) blendet nur aus, App bleibt im Tray.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_window is { IsExiting: false })
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    private void ExitApp()
    {
        if (_window is not null) _window.IsExiting = true;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    private static Icon LoadIcon()
    {
        // Eigenes Icon optional unter "app.ico" – sonst System-Schild-Icon.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { /* Fallback unten */ }
        return SystemIcons.Shield;
    }
}

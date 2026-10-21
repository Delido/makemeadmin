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
            Text = "RunAsAdmin – keine Adminrechte aktiv",
            Icon = LoadIcon(active: false),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Programme starten…", null, (_, _) => ShowWindow());
        menu.Items.Add("Einstellungen (Eltern)…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;

        // Admin-Status regelmäßig abfragen und Tray-Icon/Tooltip anpassen.
        StartStatusPolling();

        // Stündlich im Hintergrund nach Updates suchen und still einspielen.
        StartAutoUpdate();

        // Beim Start: nur ins Tray, wenn mit /tray gestartet (z. B. Autostart),
        // sonst Fenster direkt zeigen.
        if (!e.Args.Contains("/tray"))
            ShowWindow();
    }

    private System.Windows.Threading.DispatcherTimer? _statusTimer;
    private bool _lastActive;

    private void StartStatusPolling()
    {
        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _statusTimer.Tick += async (_, _) =>
        {
            bool active = false;
            try { active = (await RunAsAdmin.Shared.PipeClient.TimeStatusAsync()).Active; } catch { }
            if (active != _lastActive && _tray is not null)
            {
                _lastActive = active;
                _tray.Icon = LoadIcon(active);
                _tray.Text = active
                    ? "RunAsAdmin – ADMINRECHTE AKTIV"
                    : "RunAsAdmin – keine Adminrechte aktiv";
            }
        };
        _statusTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    private void StartAutoUpdate()
    {
        // Erste Prüfung nach 2 Minuten (nach dem Start), danach stündlich.
        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(2),
        };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(1); // ab jetzt stündlich
            await CheckAndApplyUpdateAsync();
        };
        _updateTimer.Start();
    }

    private async Task CheckAndApplyUpdateAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "1.0.0";
            var info = await RunAsAdmin.Shared.Updater.CheckAsync(current);
            if (!info.Available || info.DownloadUrl is null) return;

            // Keine Updates während einer aktiven Admin-Zeit einspielen (Dienst-Neustart).
            try { if ((await RunAsAdmin.Shared.PipeClient.TimeStatusAsync()).Active) return; } catch { }

            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RunAsAdminUpdate");
            var zip = await RunAsAdmin.Shared.Updater.DownloadAsync(info.DownloadUrl, dir);
            await RunAsAdmin.Shared.PipeClient.ApplyUpdateAsync(zip);
            // Der Dienst spielt ein und startet Dienst + Tray-GUI neu.
        }
        catch { /* still – nächste Stunde erneut */ }
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

    private static Icon LoadIcon(bool active)
    {
        // Aktiv: rotes Warnschild (Adminrechte an). Inaktiv: normales app.ico.
        try
        {
            if (active) return BuildStatusIcon(System.Drawing.Color.FromArgb(220, 60, 50));
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
            return BuildStatusIcon(System.Drawing.Color.FromArgb(42, 91, 215));
        }
        catch { return SystemIcons.Shield; }
    }

    /// <summary>Zeichnet ein einfaches rundes Schild-Icon in der gewünschten Farbe.</summary>
    private static Icon BuildStatusIcon(System.Drawing.Color color)
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 28, 28);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3)
        { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new System.Drawing.Point(10, 17), new System.Drawing.Point(14, 22), new System.Drawing.Point(23, 11),
        });
        return Icon.FromHandle(bmp.GetHicon());
    }
}

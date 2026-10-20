using System.IO;
﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Gui;

public partial class MainWindow : Window
{
    /// <summary>true, wenn die App wirklich beendet wird (nicht nur ins Tray).</summary>
    public bool IsExiting { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadTilesAsync();
    }

    private async Task LoadTilesAsync()
    {
        Tiles.Items.Clear();
        AppListResponse list;
        try
        {
            list = await PipeClient.ListAppsAsync();
        }
        catch (Exception ex)
        {
            ShowResult(false, "Dienst nicht erreichbar",
                "Der RunAsAdmin-Dienst läuft nicht. Bitte einen Erwachsenen fragen.\n\n" + ex.Message);
            return;
        }

        // Sonderkachel: 15 Minuten volle Adminrechte (immer mit Freigabe).
        var timeTile = new TileButton
        {
            Style = (Style)FindResource("Tile"),
            Content = "15 Minuten Admin",
            Hint = "Papa muss erlauben",
            Glyph = "⏱",
            TileKind = 3, // orange
            Tag = "__TIME__",
        };
        timeTile.Click += TimeTile_Click;
        Tiles.Items.Add(timeTile);
        _ = RefreshTimeStatusAsync(timeTile);

        // Nur installierte Programme anzeigen (nicht gefundene ausblenden).
        var available = list.Apps.Where(a => a.Available).ToList();
        foreach (var app in available)
        {
            var btn = new TileButton
            {
                Style = (Style)FindResource("Tile"),
                Content = app.Name,
                Hint = app.AutoApprove ? "startet sofort" : "Papa muss kurz erlauben",
                Glyph = app.AutoApprove ? "⚡" : "🔒",
                IsEnabled = true,
                Tag = app,
                TileKind = app.AutoApprove ? 0 : 1,
            };
            btn.Click += Tile_Click;
            Tiles.Items.Add(btn);
        }
        Footer.Text = $"{available.Count} Programme · RunAsAdmin";
    }

    private async void TimeTile_Click(object sender, RoutedEventArgs e)
    {
        OverlayIcon.Text = "⏱";
        OverlayTitle.Text = "Warte auf Freigabe von Papa…";
        OverlayText.Text = "15 Minuten volle Adminrechte angefragt.";
        OverlayBar.Visibility = Visibility.Visible;
        OverlayClose.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
        try
        {
            var resp = await PipeClient.GrantTimeAsync();
            var ok = resp.State == RequestState.Approved;
            ShowResult(ok, ok ? "Du bist 15 Minuten Admin!" : "Nicht erlaubt", resp.Message);
            if (sender is TileButton t) _ = RefreshTimeStatusAsync(t);
        }
        catch (Exception ex)
        {
            ShowResult(false, "Es hat nicht geklappt", ex.Message);
        }
    }

    private static async Task RefreshTimeStatusAsync(TileButton tile)
    {
        try
        {
            var st = await PipeClient.TimeStatusAsync();
            if (st.Active)
            {
                tile.Content = "Admin aktiv";
                tile.Hint = $"noch {Math.Max(1, st.RemainingSeconds / 60)} Min";
            }
            else
            {
                tile.Content = "15 Minuten Admin";
                tile.Hint = "Papa muss erlauben";
            }
        }
        catch { /* Status ist optional */ }
    }

    private async void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not TileButton btn || btn.Tag is not AppInfo app) return;

        ShowWaiting(app);
        try
        {
            var resp = await PipeClient.RunAsync(app.Name);
            var ok = resp.State == RequestState.Approved;
            ShowResult(ok, ok ? "Los geht's!" : TitleFor(resp), resp.Message);
        }
        catch (Exception ex)
        {
            ShowResult(false, "Es hat nicht geklappt", ex.Message);
        }
    }

    private static string TitleFor(ElevationResponse resp) => resp.Decision switch
    {
        "Timeout" => "Keine Antwort",
        "HomeAssistant" => "Abgelehnt",
        "IntegrityCheck" => "Sicherheitsprüfung",
        _ => "Nicht erlaubt",
    };

    private void ShowWaiting(AppInfo app)
    {
        OverlayIcon.Text = app.AutoApprove ? "🚀" : "⏳";
        OverlayTitle.Text = app.AutoApprove ? $"{app.Name} wird gestartet…" : "Warte auf Freigabe von Papa…";
        OverlayText.Text = app.AutoApprove ? "" : "Du bekommst gleich Bescheid.";
        OverlayBar.Visibility = Visibility.Visible;
        OverlayClose.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void ShowResult(bool ok, string title, string text)
    {
        OverlayIcon.Text = ok ? "✅" : "🚫";
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayBar.Visibility = Visibility.Collapsed;
        OverlayClose.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OverlayClose_Click(object sender, RoutedEventArgs e)
    {
        Overlay.Visibility = Visibility.Collapsed;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "1.0.0";

        OverlayIcon.Text = "🔄";
        OverlayTitle.Text = "Suche nach Updates…";
        OverlayText.Text = $"Aktuelle Version: {current}";
        OverlayBar.Visibility = Visibility.Visible;
        OverlayClose.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;

        var info = await Updater.CheckAsync(current);
        if (!info.Available)
        {
            ShowResult(true, "Alles aktuell",
                string.IsNullOrEmpty(info.LatestVersion)
                    ? "Es wurde keine neuere Version gefunden."
                    : $"Neueste Version: {info.LatestVersion} – du hast schon die aktuelle.");
            return;
        }

        // Update gefunden – herunterladen und einspielen.
        OverlayIcon.Text = "⬇️";
        OverlayTitle.Text = $"Update {info.LatestVersion} wird geladen…";
        OverlayText.Text = info.Notes ?? "";
        OverlayBar.Visibility = Visibility.Visible;
        OverlayClose.Visibility = Visibility.Collapsed;

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "RunAsAdminUpdate");
            var zip = await Updater.DownloadAsync(info.DownloadUrl!, dir);
            var resp = await PipeClient.ApplyUpdateAsync(zip);
            var ok = resp.State == RequestState.Approved;
            ShowResult(ok, ok ? "Update wird eingespielt" : "Update fehlgeschlagen",
                ok ? "Das Programm startet gleich neu. Du kannst dieses Fenster schließen."
                   : resp.Message);
        }
        catch (Exception ex)
        {
            ShowResult(false, "Update fehlgeschlagen", ex.Message);
        }
    }
}

/// <summary>Button mit zusätzlichem Hinweistext (für das Kachel-Template).</summary>
public sealed class TileButton : System.Windows.Controls.Button
{
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(TileButton),
            new PropertyMetadata(""));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(TileButton),
            new PropertyMetadata(""));

    /// <summary>0 = sofort (grün), 1 = Freigabe nötig (blau), 2 = nicht installiert (grau).</summary>
    public static readonly DependencyProperty TileKindProperty =
        DependencyProperty.Register(nameof(TileKind), typeof(int), typeof(TileButton),
            new PropertyMetadata(0));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public int TileKind
    {
        get => (int)GetValue(TileKindProperty);
        set => SetValue(TileKindProperty, value);
    }
}

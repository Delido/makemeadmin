using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RunAsAdmin.Shared;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;

namespace RunAsAdmin.Gui;

public partial class MainWindow : Window
{
    public bool IsExiting { get; set; }
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Loaded += async (_, _) => { _statusTimer.Start(); await RefreshStatusAsync(); };
        Closed += (_, _) => _statusTimer.Stop();
    }

    // ---------- Navigation ----------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageStart is null) return; // noch nicht geladen
        PageStart.Visibility = NavStart.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGames.Visibility = NavGames.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavStart.IsChecked == true) PageTitle.Text = "Start";
        else if (NavGames.IsChecked == true) { PageTitle.Text = "Apps"; _ = LoadGamesAsync(); }
        else if (NavHistory.IsChecked == true) { PageTitle.Text = "Verlauf"; _ = LoadHistoryAsync(); }
        else if (NavStatus.IsChecked == true) { PageTitle.Text = "Status"; _ = LoadStatusAsync(); }
        else if (NavSettings.IsChecked == true)
        {
            PageTitle.Text = "Start";
            NavStart.IsChecked = true;      // Settings öffnet ein eigenes Fenster
            try
            {
                new SettingsWindow { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Einstellungen konnten nicht geöffnet werden:\n\n" + ex,
                    "RunAsAdmin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    // ---------- Admin anfragen ----------
    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        ShowOverlay("⏳", "Warte auf Freigabe von Papa…", "Das kann bis zu 2 Minuten dauern.", waiting: true, cancelable: true);
        try
        {
            var resp = await PipeClient.GrantTimeAsync(ct: _cts.Token);
            var ok = resp.State == RequestState.Approved;
            ShowOverlay(ok ? "✅" : "🚫",
                ok ? "Du bist jetzt 15 Minuten Admin!" : TitleFor(resp),
                resp.Message, waiting: false);
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            ShowOverlay("🚫", "Abgebrochen", "Du hast die Anfrage abgebrochen.", waiting: false);
        }
        catch (Exception ex)
        {
            ShowOverlay("🚫", "Es hat nicht geklappt", ex.Message, waiting: false);
        }
    }

    private static string TitleFor(TimeAdminResponse r) => r.Decision switch
    {
        "Timeout" => "Keine Antwort",
        "AlreadyActive" => "Schon aktiv",
        "Policy" => "Nicht eingerichtet",
        "PinWrong" => "Falsche PIN",
        _ => "Nicht erlaubt",
    };

    // ---------- Fallback: lokal per Eltern-PIN (wenn HA/Server nicht geht) ----------
    private async void LocalPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinPrompt.Ask(this, "Eltern-PIN eingeben, um 15 Minuten Admin zu geben:");
        if (string.IsNullOrEmpty(pin)) return;
        ShowOverlay("⏳", "Prüfe PIN…", "", waiting: true);
        try
        {
            var resp = await PipeClient.GrantTimeLocalAsync(pin);
            var ok = resp.State == RequestState.Approved;
            ShowOverlay(ok ? "✅" : "🚫",
                ok ? "Du bist jetzt 15 Minuten Admin!" : TitleFor(resp),
                resp.Message, waiting: false);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowOverlay("🚫", "Es hat nicht geklappt", ex.Message, waiting: false);
        }
    }

    // ---------- Status (Start-Seite + Tray) ----------
    private async Task RefreshStatusAsync()
    {
        try
        {
            var st = await PipeClient.TimeStatusAsync();
            if (st.Active)
            {
                var min = Math.Max(1, st.RemainingSeconds / 60);
                StatusDot.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0xBF, 0x71));
                StatusTitle.Text = "Adminrechte aktiv";
                StatusDetail.Text = $"Noch {min} Minute{(min == 1 ? "" : "n")}. Du kannst jetzt Programme als Admin starten.";
                RequestBtn.IsEnabled = false;
                RequestBtn.Content = $"Aktiv – noch {min} Min";
            }
            else
            {
                StatusDot.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x72));
                StatusTitle.Text = "Keine Adminrechte aktiv";
                StatusDetail.Text = "Du bist normaler Benutzer.";
                RequestBtn.IsEnabled = true;
                RequestBtn.Content = "Adminrechte anfragen (15 Min)";
            }
        }
        catch
        {
            StatusTitle.Text = "Dienst nicht erreichbar";
            StatusDetail.Text = "Bitte einen Erwachsenen fragen.";
        }

        // Warnen, wenn das Konto dauerhaft Admin ist (ändert sich selten) – daher nur
        // alle ~30 s prüfen, nicht bei jedem Status-Tick. Entlastet die Pipe deutlich.
        if (DateTime.UtcNow - _lastAdminCheck > TimeSpan.FromSeconds(30))
        {
            _lastAdminCheck = DateTime.UtcNow;
            try
            {
                var chk = await PipeClient.AdminCheckAsync();
                AdminWarn.Visibility = chk.IsPermanentAdmin ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
    }

    private DateTime _lastAdminCheck = DateTime.MinValue;

    private async void Demote_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinPrompt.Ask(this, "Eltern-PIN zum Herunterstufen eingeben:");
        if (string.IsNullOrEmpty(pin)) return;
        try
        {
            var resp = await PipeClient.DemoteAsync(pin);
            var ok = resp.State == RequestState.Approved;
            ShowOverlay(ok ? "✅" : "🚫", ok ? "Heruntergestuft" : "Nicht möglich", resp.Message, waiting: false);
            if (ok) AdminWarn.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ShowOverlay("🚫", "Fehler", ex.Message, waiting: false); }
    }

    // ---------- Verlauf ----------
    private async Task LoadHistoryAsync()
    {
        HistoryList.Items.Clear();
        HistoryResponse hist;
        try { hist = await PipeClient.HistoryAsync(); }
        catch { return; }

        if (hist.Items.Count == 0)
        {
            HistoryList.Items.Add(MutedText("Noch keine Ereignisse heute."));
            return;
        }
        foreach (var it in hist.Items)
            HistoryList.Items.Add(HistoryRow(it));
    }

    private static UIElement HistoryRow(HistoryItem it)
    {
        var dot = it.Kind switch
        {
            "grant" => Color.FromRgb(0x2F, 0xBF, 0x71),
            "deny" => Color.FromRgb(0xE0, 0x5A, 0x4A),
            _ => Color.FromRgb(0x5A, 0x63, 0x72),
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "●", Foreground = new SolidColorBrush(dot), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        panel.Children.Add(new TextBlock { Text = it.Time, Foreground = new SolidColorBrush(Color.FromRgb(0x8A,0x93,0xA1)),
            FontSize = 12.5, Width = 44, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = it.Text, Foreground = new SolidColorBrush(Color.FromRgb(0xE6,0xE9,0xEE)),
            FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return new Border { Padding = new Thickness(4, 2, 4, 2), Child = panel };
    }

    private static TextBlock MutedText(string t) => new()
    { Text = t, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA1)), FontSize = 13 };

    // ---------- Spiele-Seite ----------
    private async Task LoadGamesAsync()
    {
        GamesList.Items.Clear();
        AppListResponse list;
        try { list = await PipeClient.ListAppsAsync(); }
        catch { GamesEmpty.Visibility = Visibility.Visible; return; }

        // Nur dauerhaft freigegebene Programme ("für immer erlaubt").
        var games = list.Apps.Where(a => a.AutoApprove && !string.IsNullOrWhiteSpace(a.Path)).ToList();
        GamesEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in games)
            GamesList.Items.Add(GameTile(g));
    }

    private static string CategoryIcon(AppCategory c) => c switch
    {
        AppCategory.Spiel => "🎮",
        AppCategory.Tool => "🛠️",
        _ => "📦",
    };

    private UIElement GameTile(AppInfo g)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Kategorie-Icon
        var icon = new TextBlock
        {
            Text = CategoryIcon(g.Category),
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = g.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEE)),
            FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = g.Available ? $"{g.Category} · dauerhaft freigegeben" : "Datei nicht gefunden",
            Foreground = new SolidColorBrush(g.Available
                ? Color.FromRgb(0x2F, 0xBF, 0x71) : Color.FromRgb(0xE0, 0x5A, 0x4A)),
            FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var btn = new Button
        {
            Content = g.Category == AppCategory.Spiel ? "▶  Spielen" : "▶  Starten",
            IsEnabled = g.Available,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0xBF, 0x71)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(18, 8, 18, 8),
            FontWeight = FontWeights.SemiBold,
            Tag = g.Path,
        };
        btn.Click += Game_Play;
        Grid.SetColumn(btn, 2);
        grid.Children.Add(btn);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
        };
    }

    private async void Game_Play(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path || string.IsNullOrEmpty(path)) return;
        ShowOverlay("🚀", "Wird gestartet…", "", waiting: true);
        try
        {
            var resp = await PipeClient.LaunchGameAsync(path);
            var ok = resp.State == RequestState.Approved;
            ShowOverlay(ok ? "✅" : "🚫", ok ? "Viel Spaß!" : "Start fehlgeschlagen",
                resp.Message, waiting: false);
        }
        catch (Exception ex)
        {
            ShowOverlay("🚫", "Es hat nicht geklappt", ex.Message, waiting: false);
        }
    }

    // ---------- Status-Seite ----------
    private async Task LoadStatusAsync()
    {
        StatusInfo.Children.Clear();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        StatusInfo.Children.Add(InfoRow("Version", version));
        try
        {
            var s = await PipeClient.GetSettingsAsync();
            var ha = s.Settings;
            StatusInfo.Children.Add(InfoRow("Home Assistant", ha?.Enabled == true ? "verbunden" : "nicht aktiv"));
            StatusInfo.Children.Add(InfoRow("Adresse", string.IsNullOrEmpty(ha?.BaseUrl) ? "—" : ha!.BaseUrl));
            StatusInfo.Children.Add(InfoRow("Geheimnis", ha?.SecretIsSet == true ? "gesetzt" : "fehlt"));
        }
        catch { StatusInfo.Children.Add(InfoRow("Dienst", "nicht erreichbar")); }
    }

    private static UIElement InfoRow(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x8A,0x93,0xA1)), FontSize = 13 };
        var v = new TextBlock { Text = value, Foreground = new SolidColorBrush(Color.FromRgb(0xE6,0xE9,0xEE)), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
        return g;
    }

    // ---------- Update ----------
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        ShowOverlay("🔄", "Suche nach Updates…", $"Aktuelle Version: {current}", waiting: true);
        var info = await Updater.CheckAsync(current);
        if (!info.Available)
        {
            ShowOverlay("✅", "Alles aktuell",
                string.IsNullOrEmpty(info.LatestVersion) ? "Keine neuere Version gefunden."
                    : $"Neueste Version: {info.LatestVersion}.", waiting: false);
            return;
        }
        ShowOverlay("⬇️", $"Update {info.LatestVersion} wird eingespielt…", info.Notes ?? "", waiting: true);
        try
        {
            // Der Dienst lädt + prüft + spielt selbst ein (sicher).
            var resp = await PipeClient.ApplyUpdateAsync();
            var ok = resp.State == RequestState.Approved;
            ShowOverlay(ok ? "✅" : "🚫", ok ? "Update wird eingespielt" : "Fehlgeschlagen",
                ok ? "Das Programm startet gleich neu." : resp.Message, waiting: false);
        }
        catch (Exception ex) { ShowOverlay("🚫", "Update fehlgeschlagen", ex.Message, waiting: false); }
    }

    // ---------- Overlay-Helfer ----------
    private void ShowOverlay(string icon, string title, string text, bool waiting, bool cancelable = false)
    {
        OverlayIcon.Text = icon;
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayBar.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        OverlayCancel.Visibility = (waiting && cancelable) ? Visibility.Visible : Visibility.Collapsed;
        OverlayClose.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OverlayClose_Click(object sender, RoutedEventArgs e) => Overlay.Visibility = Visibility.Collapsed;
    private void OverlayCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- Fenster ----------
    private void TitleBar_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}

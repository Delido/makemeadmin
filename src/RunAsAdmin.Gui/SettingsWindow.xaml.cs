using System.Windows;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Gui;

/// <summary>
/// Eltern-Einstellungen: HA-Adresse, Secret, Webhooks, PIN. Speichern geht nur
/// über den Dienst (SYSTEM) und nur nach PIN-Prüfung. Das Secret wird nie im
/// Klartext angezeigt (nur „gesetzt/nicht gesetzt").
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _pinIsSet;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        SettingsResponse resp;
        try { resp = await PipeClient.GetSettingsAsync(); }
        catch (Exception ex) { Status.Text = "Dienst nicht erreichbar: " + ex.Message; return; }

        _pinIsSet = resp.PinIsSet;
        var s = resp.Settings ?? new SettingsPayload();

        ModeHa.IsChecked = s.Mode == ApprovalMode.HomeAssistant;
        ModeServer.IsChecked = s.Mode == ApprovalMode.Server;

        BaseUrl.Text = s.BaseUrl;
        Enabled.IsChecked = s.Enabled;
        ReqWebhook.Text = s.RequestWebhookId;
        EvtWebhook.Text = s.EventWebhookId;
        Port.Text = s.CallbackPort.ToString();
        SecretHint.Text = s.SecretIsSet
            ? "Ein Geheimnis ist gesetzt. Feld leer lassen = unveraendert."
            : "Noch kein Geheimnis gesetzt.";

        ServerUrl.Text = s.ServerBaseUrl;
        ServerTokenHint.Text = s.ServerTokenIsSet
            ? "Ein Token ist gesetzt. Feld leer lassen = unveraendert."
            : "Noch kein Token gesetzt.";

        UpdateModeVisibility();

        if (_pinIsSet)
        {
            PinInfo.Text = "Zum Speichern die aktuelle PIN eingeben.";
            CurrentPinLabel.Visibility = Visibility.Visible;
            CurrentPin.Visibility = Visibility.Visible;
        }
        else
        {
            PinInfo.Text = "Erste Einrichtung: bitte eine neue Eltern-PIN (mind. 4 Zeichen) vergeben.";
            CurrentPinLabel.Visibility = Visibility.Collapsed;
            CurrentPin.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Blendet je nach gewähltem Modus die passende Feldgruppe ein.</summary>
    private void UpdateModeVisibility()
    {
        var server = ModeServer.IsChecked == true;
        HaGroup.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        ServerGroup.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateModeVisibility();

    /// <summary>Erzeugt ein starkes Zufalls-Geheimnis und zeigt es zum Kopieren an.</summary>
    private void GenSecret_Click(object sender, RoutedEventArgs e)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var secret = Convert.ToHexString(bytes).ToLowerInvariant();
        SecretBox.Password = secret;
        SecretPlain.Text = $"Erzeugt: {secret}\nDiesen Wert in Home Assistant eintragen, dann hier speichern.";
        SecretPlain.Visibility = Visibility.Visible;
        try { System.Windows.Clipboard.SetText(secret); SecretPlain.Text += " (in Zwischenablage kopiert)"; }
        catch { }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var server = ModeServer.IsChecked == true;

        // Port nur im HA-Modus relevant; im Server-Modus unveraendert durchreichen.
        int port = 0;
        if (!server && (!int.TryParse(Port.Text.Trim(), out port) || port <= 0))
        {
            Status.Text = "Port ungueltig.";
            return;
        }

        var payload = new SettingsPayload
        {
            Mode = server ? ApprovalMode.Server : ApprovalMode.HomeAssistant,
            BaseUrl = BaseUrl.Text.Trim(),
            Enabled = Enabled.IsChecked == true,
            RequestWebhookId = ReqWebhook.Text.Trim(),
            EventWebhookId = EvtWebhook.Text.Trim(),
            CallbackPort = port,
            ReportEvents = true,
            ServerBaseUrl = ServerUrl.Text.Trim(),
            Pin = _pinIsSet ? CurrentPin.Password : null,
            NewPin = string.IsNullOrEmpty(NewPin.Password) ? null : NewPin.Password,
            NewSecret = string.IsNullOrEmpty(SecretBox.Password) ? null : SecretBox.Password,
            NewServerToken = string.IsNullOrEmpty(ServerTokenBox.Password) ? null : ServerTokenBox.Password,
        };

        SaveBtn.IsEnabled = false;
        Status.Foreground = System.Windows.Media.Brushes.Gray;
        Status.Text = "Speichere…";
        try
        {
            var resp = await PipeClient.SaveSettingsAsync(payload);
            if (resp.State == RequestState.Approved)
            {
                Status.Foreground = System.Windows.Media.Brushes.LightGreen;
                Status.Text = resp.Message;
                await Task.Delay(700);
                Close();
            }
            else
            {
                Status.Foreground = System.Windows.Media.Brushes.OrangeRed;
                Status.Text = resp.Message;
            }
        }
        catch (Exception ex)
        {
            Status.Foreground = System.Windows.Media.Brushes.OrangeRed;
            Status.Text = ex.Message;
        }
        finally { SaveBtn.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

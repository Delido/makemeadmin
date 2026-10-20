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

        BaseUrl.Text = s.BaseUrl;
        Enabled.IsChecked = s.Enabled;
        ReqWebhook.Text = s.RequestWebhookId;
        EvtWebhook.Text = s.EventWebhookId;
        Port.Text = s.CallbackPort.ToString();
        SecretHint.Text = s.SecretIsSet
            ? "Ein Geheimnis ist gesetzt. Feld leer lassen = unveraendert."
            : "Noch kein Geheimnis gesetzt.";

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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Port.Text.Trim(), out var port) || port <= 0)
        {
            Status.Text = "Port ungueltig.";
            return;
        }

        var payload = new SettingsPayload
        {
            BaseUrl = BaseUrl.Text.Trim(),
            Enabled = Enabled.IsChecked == true,
            RequestWebhookId = ReqWebhook.Text.Trim(),
            EventWebhookId = EvtWebhook.Text.Trim(),
            CallbackPort = port,
            ReportEvents = true,
            Pin = _pinIsSet ? CurrentPin.Password : null,
            NewPin = string.IsNullOrEmpty(NewPin.Password) ? null : NewPin.Password,
            NewSecret = string.IsNullOrEmpty(SecretBox.Password) ? null : SecretBox.Password,
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

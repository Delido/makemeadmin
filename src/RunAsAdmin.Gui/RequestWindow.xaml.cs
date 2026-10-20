using System.IO;
using System.Windows;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Gui;

/// <summary>
/// Dialog für den Explorer-Rechtsklick „Mit Adminrechten anfragen". Zeigt die
/// gewählte Datei, schickt die Anfrage an den Dienst und zeigt den Warte-/Ergebnisstatus.
/// </summary>
public partial class RequestWindow : Window
{
    private readonly string _path;

    public RequestWindow(string path)
    {
        InitializeComponent();
        _path = path;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            TitleText.Text = "Datei nicht gefunden";
            AppText.Text = path;
            ShowClose();
        }
        else
        {
            AppText.Text = Path.GetFileName(path);
        }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        Buttons.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible;
        Icon.Text = "⏳";
        TitleText.Text = "Warte auf Freigabe von Papa…";
        AppText.Text = $"„{Path.GetFileName(_path)}" + "\"";

        try
        {
            var resp = await PipeClient.RequestPathAsync(_path);
            var ok = resp.State == RequestState.Approved;
            Icon.Text = ok ? "✅" : "🚫";
            TitleText.Text = ok ? "Erlaubt!" : "Nicht erlaubt";
            AppText.Text = resp.Message;
        }
        catch (Exception ex)
        {
            Icon.Text = "🚫";
            TitleText.Text = "Es hat nicht geklappt";
            AppText.Text = ex.Message;
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            ShowClose();
        }
    }

    private void ShowClose()
    {
        Buttons.Visibility = Visibility.Collapsed;
        CloseBtn.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

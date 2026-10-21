using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace RunAsAdmin.Gui;

/// <summary>Minimaler PIN-Eingabedialog (modal).</summary>
public static class PinPrompt
{
    public static string? Ask(Window owner, string message)
    {
        var win = new Window
        {
            Title = "PIN", Width = 340, Height = 180, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x20, 0x2A)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), Padding = new Thickness(22),
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = message, Foreground = System.Windows.Media.Brushes.White, FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        var pb = new PasswordBox
        {
            FontSize = 16, Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x11, 0x16)),
            Foreground = System.Windows.Media.Brushes.White,
            CaretBrush = System.Windows.Media.Brushes.White,
        };
        panel.Children.Add(pb);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        string? result = null;
        var ok = new Button { Content = "OK", Width = 80, Height = 34, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = System.Windows.Media.Brushes.White, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x5B, 0xD7)), BorderThickness = new Thickness(0) };
        var cancel = new Button { Content = "Abbrechen", Width = 100, Height = 34, Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = System.Windows.Media.Brushes.LightGray, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x30, 0x3A)), BorderThickness = new Thickness(0) };
        ok.Click += (_, _) => { result = pb.Password; win.Close(); };
        cancel.Click += (_, _) => { result = null; win.Close(); };
        pb.KeyDown += (_, ev) => { if (ev.Key == System.Windows.Input.Key.Enter) { result = pb.Password; win.Close(); } };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        border.Child = panel;
        win.Content = border;
        pb.Loaded += (_, _) => pb.Focus();
        win.ShowDialog();
        return result;
    }
}

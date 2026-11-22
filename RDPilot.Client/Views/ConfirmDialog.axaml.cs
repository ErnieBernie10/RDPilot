using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDPilot.Client.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> ShowAsync(Window owner, string heading, string message, string confirmText, bool destructive)
    {
        var dialog = new ConfirmDialog { Title = heading };
        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.ConfirmButton.Classes.Add(destructive ? "Danger" : "accent");
        return dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}

using Avalonia.Controls;
using RDP.Client.Models;
using RDP.Client.ViewModels;

namespace RDP.Client.Views;

public partial class ConnectionEditorWindow : Window
{
    public ConnectionEditorWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ConnectionEditorViewModel vm)
        {
            Close(null);
            return;
        }

        var result = vm.BuildResult();
        if (result != null)
        {
            Close(result);
        }
    }

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }
}

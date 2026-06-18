using Avalonia.Controls;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

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

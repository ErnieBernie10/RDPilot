using Avalonia.Controls;
using Avalonia.Interactivity;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class ConnectionsPanelView : UserControl
{
    public ConnectionsPanelView()
    {
        InitializeComponent();
    }

    private async void OnAddConnectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await ConnectionDialogs.AddAsync(this, vm);
        }
    }
}

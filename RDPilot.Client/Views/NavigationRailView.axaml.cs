using Avalonia.Controls;
using Avalonia.Interactivity;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class NavigationRailView : UserControl
{
    public NavigationRailView()
    {
        InitializeComponent();
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            return;
        }

        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(vm.CreateSettingsSnapshot()),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = await window.ShowDialog<AppSettings?>(owner);
        if (result != null)
        {
            await vm.SaveSettingsAsync(result);
        }
    }
}

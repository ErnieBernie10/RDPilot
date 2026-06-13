using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RDP.Client.Models;
using RDP.Client.ViewModels;

namespace RDP.Client.Views;

public partial class ConnectionsPanelView : UserControl
{
    public ConnectionsPanelView()
    {
        InitializeComponent();
    }

    private async void OnAddConnectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var result = await ShowConnectionEditorAsync(new ConnectionEditorViewModel());
        if (result != null)
        {
            await vm.SaveConnectionAsync(result);
        }
    }

    private async void OnEditConnectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedConnection == null)
        {
            return;
        }

        var result = await ShowConnectionEditorAsync(new ConnectionEditorViewModel(vm.SelectedConnection));
        if (result != null)
        {
            await vm.SaveConnectionAsync(result);
        }
    }

    private async void OnDeleteConnectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.DeleteSelectedConnectionAsync();
        }
    }

    private Task<ConnectionEditResult?> ShowConnectionEditorAsync(ConnectionEditorViewModel viewModel)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new ConnectionEditorWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        return owner != null
            ? window.ShowDialog<ConnectionEditResult?>(owner)
            : Task.FromResult<ConnectionEditResult?>(null);
    }
}

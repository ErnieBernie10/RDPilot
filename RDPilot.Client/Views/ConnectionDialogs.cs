using Avalonia;
using Avalonia.Controls;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

internal static class ConnectionDialogs
{
    public static async Task AddAsync(Visual anchor, MainWindowViewModel vm)
    {
        var result = await ShowEditorAsync(anchor, new ConnectionEditorViewModel(globalQualitySettings: vm.CreateSettingsSnapshot().QualitySettings));
        if (result != null)
        {
            await vm.SaveConnectionAsync(result);
        }
    }

    public static async Task EditAsync(Visual anchor, MainWindowViewModel vm, SavedConnection connection)
    {
        var result = await ShowEditorAsync(anchor, new ConnectionEditorViewModel(connection, vm.CreateSettingsSnapshot().QualitySettings));
        if (result != null)
        {
            await vm.SaveConnectionAsync(result);
        }
    }

    public static async Task DeleteAsync(Visual anchor, MainWindowViewModel vm, SavedConnection connection)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return;

        var confirmed = await ConfirmDialog.ShowAsync(owner,
            $"Delete {connection.Name}?",
            "The saved connection and its stored passwords are removed. Open sessions stay connected.",
            "Delete",
            destructive: true);
        if (confirmed)
        {
            await vm.DeleteConnectionAsync(connection);
        }
    }

    private static Task<ConnectionEditResult?> ShowEditorAsync(Visual anchor, ConnectionEditorViewModel viewModel)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return Task.FromResult<ConnectionEditResult?>(null);
        }

        var window = new ConnectionEditorWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        return window.ShowDialog<ConnectionEditResult?>(owner);
    }
}

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class SessionTabsView : UserControl
{
    public event EventHandler? FullscreenToggleRequested;

    public SessionTabsView()
    {
        InitializeComponent();
    }

    private async void OnTabCloseClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { CommandParameter: RdpSessionViewModel session } || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var shouldClose = !session.CanDisconnect || await ConfirmCloseSessionAsync(session);
        if (shouldClose && vm.CloseSessionCommand.CanExecute(session))
        {
            await vm.CloseSessionCommand.ExecuteAsync(session);
        }
    }

    private void OnFullscreenToggleClicked(object? sender, RoutedEventArgs e)
    {
        FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private Task<bool> ConfirmCloseSessionAsync(RdpSessionViewModel session)
    {
        return TopLevel.GetTopLevel(this) is Window owner
            ? ConfirmDialog.ShowAsync(owner,
                $"Disconnect from {session.Title}?",
                "Closing this tab ends the remote session. Unsaved work on the remote computer stays open there.",
                "Disconnect and close",
                destructive: false)
            : Task.FromResult(false);
    }
}

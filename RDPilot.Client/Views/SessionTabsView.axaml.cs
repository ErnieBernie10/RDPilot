using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class SessionTabsView : UserControl
{
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

        var shouldClose = await ConfirmCloseSessionAsync(session);
        if (shouldClose && vm.CloseSessionCommand.CanExecute(session))
        {
            await vm.CloseSessionCommand.ExecuteAsync(session);
        }
    }

    private Task<bool> ConfirmCloseSessionAsync(RdpSessionViewModel session)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            return Task.FromResult(false);
        }

        var window = new Window
        {
            Title = "Close connection",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var message = new TextBlock
        {
            Text = $"Close connection \"{session.Title}\"?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var noButton = new Button
        {
            Content = "No",
            MinWidth = 84,
            Classes = { "Compact" }
        };
        noButton.Click += (_, _) => window.Close(false);

        var yesButton = new Button
        {
            Content = "Yes",
            MinWidth = 84,
            Classes = { "Accent", "Compact" }
        };
        yesButton.Click += (_, _) => window.Close(true);

        buttons.Children.Add(noButton);
        buttons.Children.Add(yesButton);
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 16,
            Children =
            {
                message,
                buttons
            }
        };

        return window.ShowDialog<bool>(owner);
    }
}

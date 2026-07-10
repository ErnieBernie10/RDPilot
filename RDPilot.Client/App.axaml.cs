using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;

namespace RDPilot.Client;

public partial class App : Application
{
    private static MainWindow? _mainWindow;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launchOptions = LaunchOptions.Parse(desktop.Args ?? []);
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(launchOptions, new WindowsJumpListService()),
            };
            _mainWindow = mainWindow;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static Task HandleConnectionRequestAsync(string connectionId)
    {
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mainWindow?.DataContext is not MainWindowViewModel viewModel)
            {
                return Task.CompletedTask;
            }

            _mainWindow.Show();
            if (_mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                _mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
            }
            _mainWindow.Activate();
            return viewModel.ConnectByIdAsync(connectionId);
        });
    }
}

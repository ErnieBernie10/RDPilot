using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RDPilot.Client.Views;

public partial class MainWindow : Window
{
    private readonly SessionTabsView _sessionToolbar;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;

    public MainWindow()
    {
        InitializeComponent();

        _sessionToolbar = new SessionTabsView();
        _sessionToolbar.FullscreenToggleRequested += OnFullscreenToggleRequested;
        SessionToolbarHost.Child = _sessionToolbar;
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, true);

        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void OnFullscreenToggleRequested(object? sender, EventArgs e)
    {
        SetFullscreen(!_isFullscreen);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullscreen(!_isFullscreen);
            e.Handled = true;
        }
        else if (_isFullscreen && e.Key == Key.Escape)
        {
            SetFullscreen(false);
            e.Handled = true;
        }
    }

    private void SetFullscreen(bool isFullscreen)
    {
        if (_isFullscreen == isFullscreen)
        {
            return;
        }

        if (isFullscreen)
        {
            _windowStateBeforeFullscreen = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _windowStateBeforeFullscreen;
        }

        _isFullscreen = isFullscreen;
        ApplyFullscreenLayout();
    }

    private void ApplyFullscreenLayout()
    {
        NavigationRail.IsVisible = !_isFullscreen;
        StatusBar.IsVisible = !_isFullscreen;
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(_isFullscreen ? 0 : 32);
        RootLayout.RowDefinitions[1].Height = _isFullscreen ? new GridLength(0) : GridLength.Auto;
        Grid.SetRowSpan(SessionToolbarHost, _isFullscreen ? 2 : 1);
        FullscreenRevealZone.IsVisible = _isFullscreen;

        if (_isFullscreen)
        {
            SessionToolbarHost.Classes.Add("FullscreenSessionToolbar");
            FullscreenRevealZone.Classes.Add("FullscreenRevealZone");
        }
        else
        {
            SessionToolbarHost.Classes.Remove("FullscreenSessionToolbar");
            FullscreenRevealZone.Classes.Remove("FullscreenRevealZone");
        }
    }
}

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace RDPilot.Client.Views;

public partial class MainWindow : Window
{
    private readonly SessionTabsView _sessionToolbar;
    private readonly DispatcherTimer _toolbarHideTimer;
    private readonly HashSet<Key> _locallyHandledFullscreenKeys = [];
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private WindowState _lastNonFullscreenWindowState = WindowState.Normal;
    private bool _isFullscreen;

    public MainWindow()
    {
        InitializeComponent();

        _sessionToolbar = new SessionTabsView();
        _sessionToolbar.FullscreenToggleRequested += OnFullscreenToggleRequested;
        SessionToolbarHost.Child = _sessionToolbar;
        _toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _toolbarHideTimer.Tick += OnToolbarHideTimerTick;
        SessionToolbarHost.PointerEntered += OnSessionToolbarPointerEntered;
        SessionToolbarHost.PointerExited += OnSessionToolbarPointerExited;
        SessionToolbarHost.GotFocus += OnSessionToolbarGotFocus;
        SessionToolbarHost.LostFocus += OnSessionToolbarLostFocus;
        FullscreenRevealZone.PointerEntered += OnFullscreenRevealZonePointerEntered;
        FullscreenRevealZone.PointerExited += OnFullscreenRevealZonePointerExited;
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel, true);
        PropertyChanged += OnWindowPropertyChanged;
        Deactivated += OnWindowDeactivated;

        Closed += (_, _) =>
        {
            _toolbarHideTimer.Stop();
            _toolbarHideTimer.Tick -= OnToolbarHideTimerTick;
            PropertyChanged -= OnWindowPropertyChanged;
            Deactivated -= OnWindowDeactivated;
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void OnFullscreenToggleRequested(object? sender, EventArgs e)
    {
        SetFullscreen(WindowState != WindowState.FullScreen);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_locallyHandledFullscreenKeys.Contains(e.Key))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            _locallyHandledFullscreenKeys.Add(e.Key);
            SetFullscreen(WindowState != WindowState.FullScreen);
            e.Handled = true;
        }
        else if (WindowState == WindowState.FullScreen && e.Key == Key.Escape)
        {
            _locallyHandledFullscreenKeys.Add(e.Key);
            SetFullscreen(false);
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_locallyHandledFullscreenKeys.Remove(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _locallyHandledFullscreenKeys.Clear();
    }

    private void SetFullscreen(bool isFullscreen)
    {
        if (isFullscreen && WindowState != WindowState.FullScreen)
        {
            _windowStateBeforeFullscreen = WindowState == WindowState.Minimized
                ? _lastNonFullscreenWindowState
                : WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (!isFullscreen && WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullscreen;
        }

        SynchronizeFullscreenState();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            SynchronizeFullscreenState();
        }
    }

    private void SynchronizeFullscreenState()
    {
        var isFullscreen = WindowState == WindowState.FullScreen;
        if (!isFullscreen && WindowState != WindowState.Minimized)
        {
            _lastNonFullscreenWindowState = WindowState;
        }

        if (_isFullscreen == isFullscreen)
        {
            return;
        }

        if (isFullscreen)
        {
            _windowStateBeforeFullscreen = _lastNonFullscreenWindowState;
        }

        _isFullscreen = isFullscreen;
        ApplyFullscreenLayout();

        if (_isFullscreen)
        {
            MoveFocusOutsideFullscreenToolbar();
            HideFullscreenToolbar(force: true);
        }
        else
        {
            _toolbarHideTimer.Stop();
            SessionToolbarHost.Opacity = 1;
            SessionToolbarHost.IsHitTestVisible = true;
        }
    }

    private void MoveFocusOutsideFullscreenToolbar()
    {
        if (SessionToolbarHost.IsKeyboardFocusWithin)
        {
            RdpViewport.Focus();
        }
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

    private void OnFullscreenRevealZonePointerEntered(object? sender, PointerEventArgs e)
    {
        ShowFullscreenToolbar();
    }

    private void OnFullscreenRevealZonePointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleFullscreenToolbarHide();
    }

    private void OnSessionToolbarPointerEntered(object? sender, PointerEventArgs e)
    {
        ShowFullscreenToolbar();
    }

    private void OnSessionToolbarPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleFullscreenToolbarHide();
    }

    private void OnSessionToolbarGotFocus(object? sender, FocusChangedEventArgs e)
    {
        ShowFullscreenToolbar();
    }

    private void OnSessionToolbarLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_isFullscreen && !SessionToolbarHost.IsPointerOver)
        {
            _toolbarHideTimer.Start();
        }
    }

    private void OnToolbarHideTimerTick(object? sender, EventArgs e)
    {
        HideFullscreenToolbar();
    }

    private void ShowFullscreenToolbar()
    {
        if (!_isFullscreen) return;

        _toolbarHideTimer.Stop();
        SessionToolbarHost.Opacity = 0.96;
        SessionToolbarHost.IsHitTestVisible = true;
    }

    private void ScheduleFullscreenToolbarHide()
    {
        if (_isFullscreen && !SessionToolbarHost.IsKeyboardFocusWithin)
        {
            _toolbarHideTimer.Start();
        }
    }

    private void HideFullscreenToolbar(bool force = false)
    {
        _toolbarHideTimer.Stop();
        if (_isFullscreen && (force || (!SessionToolbarHost.IsPointerOver && !SessionToolbarHost.IsKeyboardFocusWithin)))
        {
            SessionToolbarHost.Opacity = 0;
            SessionToolbarHost.IsHitTestVisible = false;
        }
    }
}

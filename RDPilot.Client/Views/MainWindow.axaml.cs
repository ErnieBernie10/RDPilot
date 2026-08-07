using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class MainWindow : Window
{
    /// <summary>Windowed inset around the content area; dropped in fullscreen. Mirrors the
    /// <c>Margin</c> on <c>ContentGrid</c> in MainWindow.axaml.</summary>
    private static readonly Thickness ContentMargin = new(2);

    private readonly SessionTabsView _sessionToolbar;
    private readonly DispatcherTimer _toolbarHideTimer;
    private readonly HashSet<Key> _locallyHandledFullscreenKeys = [];
    private readonly IKeyboardGrab _keyboardGrab = KeyboardGrab.CreateDefault();
    private MainWindowViewModel? _subscribedViewModel;
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
        Activated += OnWindowActivated;
        DataContextChanged += OnDataContextChanged;
        _keyboardGrab.KeyIntercepted += OnGrabbedKeyIntercepted;
        Opened += OnWindowOpened;

        Closed += (_, _) =>
        {
            _toolbarHideTimer.Stop();
            _toolbarHideTimer.Tick -= OnToolbarHideTimerTick;
            PropertyChanged -= OnWindowPropertyChanged;
            Deactivated -= OnWindowDeactivated;
            Activated -= OnWindowActivated;
            DataContextChanged -= OnDataContextChanged;
            Opened -= OnWindowOpened;
            _keyboardGrab.KeyIntercepted -= OnGrabbedKeyIntercepted;
            SubscribeViewModel(null);
            _keyboardGrab.Dispose();
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle.HasValue)
        {
            _keyboardGrab.Attach(handle.Value);
        }

        PublishKeyboardGrabAvailability();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeViewModel(DataContext as MainWindowViewModel);
        PublishKeyboardGrabAvailability();
    }

    private void SubscribeViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;

        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void PublishKeyboardGrabAvailability()
    {
        if (_subscribedViewModel == null)
        {
            return;
        }

        _subscribedViewModel.IsKeyboardGrabSupported = _keyboardGrab.IsSupported;
        _subscribedViewModel.KeyboardGrabTooltip = _keyboardGrab.IsSupported
            ? "Grab keyboard so Win, Alt+Tab and Ctrl+Esc go to the remote session"
            : _keyboardGrab.UnsupportedReason ?? "Keyboard grab is unavailable.";
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsKeyboardGrabActive))
        {
            ApplyKeyboardGrabState();
        }
    }

    private void ApplyKeyboardGrabState()
    {
        var engaged = _subscribedViewModel?.IsKeyboardGrabActive == true && _keyboardGrab.IsSupported;
        _keyboardGrab.SetEngaged(engaged);
        RdpViewport.SetKeyboardGrabActive(engaged);
    }

    private void OnGrabbedKeyIntercepted(object? sender, GrabbedKeyEventArgs e)
    {
        RdpViewport.HandleGrabbedKey(e.Scancode, e.Extended, e.IsUp);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Re-arms the hook if Windows dropped it after a UI-thread stall.
        ApplyKeyboardGrabState();
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

        // The only escape route from a grab, since there is no release hotkey: clicking another
        // window deactivates us and hands the keyboard back to the local desktop.
        _subscribedViewModel?.ReleaseKeyboardGrab();
        _keyboardGrab.SetEngaged(false);
        RdpViewport.SetKeyboardGrabActive(false);
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
        // The reveal zone is pinned to the top of ContentGrid, so any margin here becomes a dead
        // strip above it - and the top edge of the screen is exactly where the pointer lands when
        // you throw it upwards to summon the toolbar.
        ContentGrid.Margin = _isFullscreen ? new Thickness(0) : ContentMargin;
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

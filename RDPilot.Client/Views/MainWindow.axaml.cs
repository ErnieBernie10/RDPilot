using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Avalonia windows dispose their fullscreen controller in the Closed event.")]
public partial class MainWindow : Window
{
    private readonly SessionTabsView _sessionToolbar;
    private readonly FullscreenWindowController _fullscreen;
    private readonly IKeyboardGrab _keyboardGrab = KeyboardGrab.CreateDefault();
    private MainWindowViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();

        _sessionToolbar = new SessionTabsView();
        _sessionToolbar.FullscreenToggleRequested += OnFullscreenToggleRequested;
        SessionToolbarHost.Child = _sessionToolbar;
        _fullscreen = new FullscreenWindowController(this, NavigationRail, StatusBar, ShellGrid,
            RootLayout, ContentGrid, SessionToolbarHost, FullscreenRevealZone, RdpViewport);
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
            _fullscreen.Dispose();
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
        _fullscreen.Toggle();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _fullscreen.HandleKeyDown(e);
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        _fullscreen.HandleKeyUp(e);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _fullscreen.OnWindowDeactivated();

        // The only escape route from a grab, since there is no release hotkey: clicking another
        // window deactivates us and hands the keyboard back to the local desktop.
        _subscribedViewModel?.ReleaseKeyboardGrab();
        _keyboardGrab.SetEngaged(false);
        RdpViewport.SetKeyboardGrabActive(false);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            _fullscreen.Synchronize();
        }
    }
}

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace RDPilot.Client.Views;

/// <summary>Owns fullscreen layout, toolbar reveal timing, and locally handled fullscreen keys.</summary>
internal sealed class FullscreenWindowController : IDisposable
{
    private static readonly Thickness ContentMargin = new(2);
    private readonly Window _window;
    private readonly NavigationRailView _navigationRail;
    private readonly Border _statusBar;
    private readonly Grid _shellGrid;
    private readonly Grid _rootLayout;
    private readonly Grid _contentGrid;
    private readonly Border _toolbar;
    private readonly Border _revealZone;
    private readonly RdpViewportView _viewport;
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly HashSet<Key> _locallyHandledKeys = [];
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private WindowState _lastNonFullscreenState = WindowState.Normal;
    private bool _isFullscreen;

    internal FullscreenWindowController(Window window, NavigationRailView navigationRail, Border statusBar,
        Grid shellGrid, Grid rootLayout, Grid contentGrid, Border toolbar, Border revealZone, RdpViewportView viewport)
    {
        _window = window;
        _navigationRail = navigationRail;
        _statusBar = statusBar;
        _shellGrid = shellGrid;
        _rootLayout = rootLayout;
        _contentGrid = contentGrid;
        _toolbar = toolbar;
        _revealZone = revealZone;
        _viewport = viewport;

        _toolbarHideTimer.Tick += OnToolbarHideTimerTick;
        _toolbar.PointerEntered += OnPointerEntered;
        _toolbar.PointerExited += OnPointerExited;
        _toolbar.GotFocus += OnToolbarGotFocus;
        _toolbar.LostFocus += OnToolbarLostFocus;
        _revealZone.PointerEntered += OnPointerEntered;
        _revealZone.PointerExited += OnPointerExited;
    }

    internal void Toggle() => SetFullscreen(_window.WindowState != WindowState.FullScreen);

    internal void SetFullscreen(bool fullscreen)
    {
        if (fullscreen && _window.WindowState != WindowState.FullScreen)
        {
            _stateBeforeFullscreen = _window.WindowState == WindowState.Minimized
                ? _lastNonFullscreenState : _window.WindowState;
            _window.WindowState = WindowState.FullScreen;
        }
        else if (!fullscreen && _window.WindowState == WindowState.FullScreen)
        {
            _window.WindowState = _stateBeforeFullscreen;
        }

        Synchronize();
    }

    internal void HandleKeyDown(KeyEventArgs e)
    {
        if (_locallyHandledKeys.Contains(e.Key))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            _locallyHandledKeys.Add(e.Key);
            Toggle();
            e.Handled = true;
        }
        else if (_window.WindowState == WindowState.FullScreen && e.Key == Key.Escape)
        {
            _locallyHandledKeys.Add(e.Key);
            SetFullscreen(false);
            e.Handled = true;
        }
    }

    internal void HandleKeyUp(KeyEventArgs e)
    {
        if (_locallyHandledKeys.Remove(e.Key)) e.Handled = true;
    }

    internal void OnWindowDeactivated() => _locallyHandledKeys.Clear();

    internal void Synchronize()
    {
        var fullscreen = _window.WindowState == WindowState.FullScreen;
        if (!fullscreen && _window.WindowState != WindowState.Minimized)
        {
            _lastNonFullscreenState = _window.WindowState;
        }

        if (_isFullscreen == fullscreen) return;
        if (fullscreen) _stateBeforeFullscreen = _lastNonFullscreenState;
        _isFullscreen = fullscreen;
        ApplyLayout();

        if (_isFullscreen)
        {
            if (_toolbar.IsKeyboardFocusWithin) _viewport.Focus();
            HideToolbar(force: true);
        }
        else
        {
            _toolbarHideTimer.Stop();
            _toolbar.Opacity = 1;
            _toolbar.IsHitTestVisible = true;
        }
    }

    private void ApplyLayout()
    {
        _navigationRail.IsVisible = !_isFullscreen;
        _statusBar.IsVisible = !_isFullscreen;
        _shellGrid.ColumnDefinitions[0].Width = new GridLength(_isFullscreen ? 0 : 32);
        _rootLayout.RowDefinitions[1].Height = _isFullscreen ? new GridLength(0) : GridLength.Auto;
        // No dead strip above the reveal zone: it must reach the screen's top edge.
        _contentGrid.Margin = _isFullscreen ? new Thickness(0) : ContentMargin;
        Grid.SetRowSpan(_toolbar, _isFullscreen ? 2 : 1);
        _revealZone.IsVisible = _isFullscreen;

        if (_isFullscreen)
        {
            _toolbar.Classes.Add("FullscreenSessionToolbar");
            _revealZone.Classes.Add("FullscreenRevealZone");
        }
        else
        {
            _toolbar.Classes.Remove("FullscreenSessionToolbar");
            _revealZone.Classes.Remove("FullscreenRevealZone");
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => ShowToolbar();
    private void OnPointerExited(object? sender, PointerEventArgs e) => ScheduleHide();
    private void OnToolbarGotFocus(object? sender, FocusChangedEventArgs e) => ShowToolbar();
    private void OnToolbarLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_isFullscreen && !_toolbar.IsPointerOver) _toolbarHideTimer.Start();
    }

    private void OnToolbarHideTimerTick(object? sender, EventArgs e) => HideToolbar();

    private void ShowToolbar()
    {
        if (!_isFullscreen) return;
        _toolbarHideTimer.Stop();
        _toolbar.Opacity = 0.96;
        _toolbar.IsHitTestVisible = true;
    }

    private void ScheduleHide()
    {
        if (_isFullscreen && !_toolbar.IsKeyboardFocusWithin) _toolbarHideTimer.Start();
    }

    private void HideToolbar(bool force = false)
    {
        _toolbarHideTimer.Stop();
        if (_isFullscreen && (force || (!_toolbar.IsPointerOver && !_toolbar.IsKeyboardFocusWithin)))
        {
            _toolbar.Opacity = 0;
            _toolbar.IsHitTestVisible = false;
        }
    }

    public void Dispose()
    {
        _toolbarHideTimer.Stop();
        _toolbarHideTimer.Tick -= OnToolbarHideTimerTick;
        _toolbar.PointerEntered -= OnPointerEntered;
        _toolbar.PointerExited -= OnPointerExited;
        _toolbar.GotFocus -= OnToolbarGotFocus;
        _toolbar.LostFocus -= OnToolbarLostFocus;
        _revealZone.PointerEntered -= OnPointerEntered;
        _revealZone.PointerExited -= OnPointerExited;
    }
}

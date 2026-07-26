using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class MainWindowFullscreenTests
{
    [Fact]
    public void FullscreenToolbarButton_EntersFullscreen()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var button = FindFullscreenToggleButton(window);
                Assert.NotNull(button);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(WindowState.FullScreen, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenToolbarButton_RestoresPreviousWindowState()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                var button = FindFullscreenToggleButton(window);
                Assert.NotNull(button);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenKeys_ToggleAndExitFullscreen()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                var f11 = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(f11);

                Assert.True(f11.Handled);
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(escape);

                Assert.True(escape.Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenKeys_DoNotReachHandledEventsTooViewportInputHandler()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();

                var f11Down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(f11Down);

                Assert.True(f11Down.Handled);
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                var f11Up = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F11 };
                window.RaiseEvent(f11Up);

                Assert.True(f11Up.Handled);

                var escapeDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(escapeDown);

                Assert.True(escapeDown.Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                var escapeUp = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Escape };
                window.RaiseEvent(escapeUp);

                Assert.True(escapeUp.Handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OtherHandledKeys_StillReachViewportHandledEventsTooInputHandler()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                var keyDown = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.A,
                    Handled = true
                };

                window.RaiseEvent(keyDown);

                Assert.False(keyDown.Handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenRevealZone_RevealsHiddenToolbar()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
                Assert.False(toolbarHost.IsHitTestVisible);

                revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerEnteredEvent, revealZone));

                Assert.True(toolbarHost.IsHitTestVisible);
                Assert.True(toolbarHost.Opacity > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenToolbarButton_FocusedEntry_HidesToolbarAndMovesFocusOutsideIt()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                var button = FindFullscreenToggleButton(window)!;
                button.Focus();
                Assert.True(button.IsFocused);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                Assert.False(toolbarHost.IsHitTestVisible);
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(button.IsFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FullscreenToolbar_HidesAfterPointerExitDelay()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = CreateFullscreenWindowWithRevealedToolbar();
            try
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(toolbarHost.IsPointerOver);
                toolbarHost.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, toolbarHost));

                await Task.Delay(300);
                Assert.True(toolbarHost.IsHitTestVisible);

                await Task.Delay(500);
                Assert.False(toolbarHost.IsPointerOver);
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(toolbarHost.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FullscreenRevealZone_HidesAfterDirectPointerExitDelay()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = CreateFullscreenWindowWithRevealedToolbar();
            try
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;

                revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, revealZone));

                await Task.Delay(300);
                Assert.True(toolbarHost.IsHitTestVisible);

                await Task.Delay(500);
                Assert.False(toolbarHost.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FullscreenRevealZone_ExitIntoToolbarCancelsPendingHide()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = CreateFullscreenWindowWithRevealedToolbar();
            try
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;

                revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, revealZone));
                await Task.Delay(300);
                toolbarHost.RaiseEvent(CreatePointerEvent(InputElement.PointerEnteredEvent, toolbarHost));
                Assert.False(GetToolbarHideTimer(window).IsEnabled);
                await Task.Delay(500);

                Assert.True(toolbarHost.IsHitTestVisible);
                Assert.True(toolbarHost.Opacity > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenF11AutoRepeat_TogglesOnceAndKeepsPressLocalUntilKeyUp()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();

                var firstDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(firstDown);
                var repeatedDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(repeatedDown);

                Assert.True(firstDown.Handled);
                Assert.True(repeatedDown.Handled);
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                var keyUp = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F11 };
                window.RaiseEvent(keyUp);
                Assert.True(keyUp.Handled);

                var nextPress = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(nextPress);
                Assert.True(nextPress.Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenEscapeAutoRepeat_ExitsOnceAndKeepsPressLocalUntilKeyUp()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var firstDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(firstDown);
                var repeatedDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(repeatedDown);

                Assert.True(firstDown.Handled);
                Assert.True(repeatedDown.Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                var keyUp = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Escape };
                window.RaiseEvent(keyUp);
                Assert.True(keyUp.Handled);

                var escapeOutsideFullscreen = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                window.RaiseEvent(escapeOutsideFullscreen);
                Assert.False(escapeOutsideFullscreen.Handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenKeyTracking_WindowDeactivationClearsLostKeyUpState()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                RaiseWindowDeactivated(window);

                var nextPress = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 };
                window.RaiseEvent(nextPress);

                Assert.True(nextPress.Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FullscreenToolbar_FocusLossOutsideHidesAfterDelay()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = CreateFullscreenWindowWithRevealedToolbar();
            try
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var button = FindFullscreenToggleButton(window)!;
                var focusTarget = new Button { Content = "Focus target" };
                window.FindControl<Grid>("RootLayout")!.Children.Add(focusTarget);
                button.Focus();
                Assert.True(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(toolbarHost.IsPointerOver);
                focusTarget.Focus();
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.True(toolbarHost.IsHitTestVisible);

                await Task.Delay(800);
                Assert.False(toolbarHost.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FullscreenExit_CancelsPendingHideAcrossReentry()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = CreateFullscreenWindowWithRevealedToolbar();
            try
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var toolbarHideTimer = GetToolbarHideTimer(window);
                toolbarHost.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, toolbarHost));
                Assert.True(toolbarHideTimer.IsEnabled);

                await Task.Delay(200);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
                Assert.NotEqual(WindowState.FullScreen, window.WindowState);
                Assert.False(toolbarHideTimer.IsEnabled);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F11 });
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
                revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerEnteredEvent, revealZone));
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                Assert.True(toolbarHost.IsHitTestVisible);

                await Task.Delay(600);
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                Assert.True(toolbarHost.IsHitTestVisible);
                Assert.True(toolbarHost.Opacity > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ExternalFullscreenEntryAndExit_SynchronizeLayoutAndToggleSemantics()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
                var navigationRail = window.FindControl<NavigationRailView>("NavigationRail")!;
                var statusBar = window.FindControl<Border>("StatusBar")!;

                window.WindowState = WindowState.FullScreen;

                Assert.False(navigationRail.IsVisible);
                Assert.False(statusBar.IsVisible);
                Assert.True(revealZone.IsVisible);
                Assert.False(toolbarHost.IsHitTestVisible);

                window.WindowState = WindowState.Normal;

                Assert.True(navigationRail.IsVisible);
                Assert.True(statusBar.IsVisible);
                Assert.False(revealZone.IsVisible);
                Assert.True(toolbarHost.IsHitTestVisible);

                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ExternalFullscreenEntry_ToggleRestoresObservedPriorWindowState()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();
                window.WindowState = WindowState.FullScreen;

                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.True(window.FindControl<NavigationRailView>("NavigationRail")!.IsVisible);
                Assert.True(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ExternalMinimizeAndRestore_KeepLayoutAndToggleSynchronizedToActualState()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow { WindowState = WindowState.Maximized };
            try
            {
                window.Show();
                var navigationRail = window.FindControl<NavigationRailView>("NavigationRail")!;
                var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;

                window.WindowState = WindowState.FullScreen;
                window.WindowState = WindowState.Minimized;

                Assert.True(navigationRail.IsVisible);
                Assert.False(revealZone.IsVisible);

                window.WindowState = WindowState.Maximized;
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.True(navigationRail.IsVisible);
                Assert.False(revealZone.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FullscreenTransition_PreservesSelectedSession()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var first = new RdpSessionViewModel(new SavedConnection { Name = "First" }, RdpSessionStatus.Disconnected);
            var second = new RdpSessionViewModel(new SavedConnection { Name = "Second" }, RdpSessionStatus.Disconnected);
            var viewModel = new MainWindowViewModel();
            viewModel.Sessions.Add(first);
            viewModel.Sessions.Add(second);
            viewModel.SelectedSession = first;

            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                var tabs = toolbarHost.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                Assert.Same(second, viewModel.SelectedSession);

                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FindFullscreenToggleButton(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Same(second, viewModel.SelectedSession);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Button? FindFullscreenToggleButton(MainWindow window)
    {
        return window.FindControl<Border>("SessionToolbarHost")?.Child?.FindControl<Button>("FullscreenToggleButton");
    }

    private static MainWindow CreateFullscreenWindowWithRevealedToolbar()
    {
        var window = new MainWindow();
        window.Show();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F11 });
        var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
        revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerEnteredEvent, revealZone));
        Assert.True(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible);
        return window;
    }

    private static DispatcherTimer GetToolbarHideTimer(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_toolbarHideTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<DispatcherTimer>(field?.GetValue(window));
    }

    private static void RaiseWindowDeactivated(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "OnWindowDeactivated",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, [window, EventArgs.Empty]);
    }

    private static PointerEventArgs CreatePointerEvent(RoutedEvent routedEvent, Visual source)
    {
        return new PointerEventArgs(
            routedEvent,
            null,
            new Pointer(1, PointerType.Mouse, true),
            source,
            new Point(),
            0,
            new PointerPointProperties(),
            KeyModifiers.None);
    }

}

using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public void FullscreenToolbarButton_FocusedEntry_HidesToolbar()
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

                Assert.False(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible);
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
        var window = AvaloniaTestEnvironment.RunOnUiThread(CreateFullscreenWindowWithRevealedToolbar);
        try
        {
            AvaloniaTestEnvironment.RunOnUiThread(() =>
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(toolbarHost.IsPointerOver);
                toolbarHost.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, toolbarHost));
            });

            await Task.Delay(300);
            AvaloniaTestEnvironment.RunPendingDispatcherJobs();
            AvaloniaTestEnvironment.RunOnUiThread(() => Assert.True(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible));

            await Task.Delay(500);
            AvaloniaTestEnvironment.RunPendingDispatcherJobs();
            AvaloniaTestEnvironment.RunOnUiThread(() =>
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                Assert.False(toolbarHost.IsPointerOver);
                Assert.False(toolbarHost.IsKeyboardFocusWithin);
                Assert.False(toolbarHost.IsHitTestVisible);
            });
        }
        finally
        {
            AvaloniaTestEnvironment.RunOnUiThread(window.Close);
        }
    }

    [Fact]
    public async Task FullscreenToolbar_FocusLossOutsideHidesAfterDelay()
    {
        var window = AvaloniaTestEnvironment.RunOnUiThread(CreateFullscreenWindowWithRevealedToolbar);
        try
        {
            AvaloniaTestEnvironment.RunOnUiThread(() =>
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
            });

            await Task.Delay(800);
            AvaloniaTestEnvironment.RunPendingDispatcherJobs();
            AvaloniaTestEnvironment.RunOnUiThread(() => Assert.False(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible));
        }
        finally
        {
            AvaloniaTestEnvironment.RunOnUiThread(window.Close);
        }
    }

    [Fact]
    public async Task FullscreenExit_CancelsPendingHideAndRestoresToolbarAfterDelay()
    {
        var window = AvaloniaTestEnvironment.RunOnUiThread(CreateFullscreenWindowWithRevealedToolbar);
        try
        {
            AvaloniaTestEnvironment.RunOnUiThread(() =>
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                toolbarHost.RaiseEvent(CreatePointerEvent(InputElement.PointerExitedEvent, toolbarHost));
            });

            await Task.Delay(200);
            AvaloniaTestEnvironment.RunOnUiThread(() =>
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 }));

            await Task.Delay(600);
            AvaloniaTestEnvironment.RunPendingDispatcherJobs();
            AvaloniaTestEnvironment.RunOnUiThread(() =>
            {
                var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
                Assert.NotEqual(WindowState.FullScreen, window.WindowState);
                Assert.True(toolbarHost.IsHitTestVisible);
                Assert.True(toolbarHost.Opacity > 0);
            });
        }
        finally
        {
            AvaloniaTestEnvironment.RunOnUiThread(window.Close);
        }
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
        var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
        revealZone.RaiseEvent(CreatePointerEvent(InputElement.PointerEnteredEvent, revealZone));
        Assert.True(window.FindControl<Border>("SessionToolbarHost")!.IsHitTestVisible);
        return window;
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

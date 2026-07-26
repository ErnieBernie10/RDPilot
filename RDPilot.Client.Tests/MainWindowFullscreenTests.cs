using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private static Button? FindFullscreenToggleButton(MainWindow window)
    {
        return window.FindControl<Border>("SessionToolbarHost")?.Child?.FindControl<Button>("FullscreenToggleButton");
    }
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class MainWindowKeyboardGrabTests
{
    public MainWindowKeyboardGrabTests()
    {
        AvaloniaTestEnvironment.EnsureInitialized();
    }

    [Fact]
    public void SessionToolbar_ExposesKeyboardGrabAndCtrlAltDelButtons()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();

                Assert.NotNull(FindToolbarButton(window, "KeyboardGrabToggleButton"));
                Assert.NotNull(FindToolbarButton(window, "SendCtrlAltDelButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void KeyboardGrabToggleButton_IsDisabledWhenThePlatformHasNoGrab()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var vm = CreateViewModel();
                window.DataContext = vm;
                window.Show();
                var button = FindToolbarButton(window, "KeyboardGrabToggleButton")!;

                Assert.Equal(vm.IsKeyboardGrabSupported, button.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(vm.KeyboardGrabTooltip));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowDeactivation_ReleasesTheKeyboardGrab()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var vm = CreateViewModel();
                window.DataContext = vm;
                window.Show();
                var session = new RdpSessionViewModel(CreateConnection(), RdpSessionStatus.Connected);
                vm.Sessions.Add(session);
                vm.SelectedSession = session;
                session.IsKeyboardGrabbed = true;
                vm.IsKeyboardGrabActive = true;

                RaiseWindowDeactivated(window);

                Assert.False(vm.IsKeyboardGrabActive);
                Assert.False(session.IsKeyboardGrabbed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchingTabs_DoesNotCarryTheGrabToTheNewSession()
    {
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var vm = CreateViewModel();
                window.DataContext = vm;
                window.Show();
                var first = new RdpSessionViewModel(CreateConnection("Alpha"), RdpSessionStatus.Connected);
                var second = new RdpSessionViewModel(CreateConnection("Beta"), RdpSessionStatus.Connected);
                vm.Sessions.Add(first);
                vm.Sessions.Add(second);
                vm.SelectedSession = first;
                first.IsKeyboardGrabbed = true;

                vm.SelectedSession = second;

                Assert.False(vm.IsKeyboardGrabActive);
                Assert.False(first.IsKeyboardGrabbed);
                Assert.False(second.IsKeyboardGrabbed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new NoSessionFactory());
    }

    private static Button? FindToolbarButton(MainWindow window, string name)
    {
        return window.FindControl<Border>("SessionToolbarHost")?.Child?.FindControl<Button>(name);
    }

    private static SavedConnection CreateConnection(string name = "Grab Window Test")
    {
        return new SavedConnection
        {
            Name = name,
            Host = $"{name.ToLowerInvariant().Replace(' ', '-')}.example.local",
            Username = "user"
        };
    }

    private static void RaiseWindowDeactivated(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "OnWindowDeactivated",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, [window, EventArgs.Empty]);
    }

    private sealed class NoSessionFactory : IRdpSessionFactory
    {
        public RdpSessionViewModel Create(
            SavedConnection connection,
            string password,
            string gatewayPassword,
            int width,
            int height,
            double renderScaling,
            int colorDepth,
            bool compression,
            bool fontSmoothing,
            bool bitmapCache,
            bool desktopWallpaper,
            bool themes,
            bool menuAnimations,
            bool fullWindowDrag,
            RdpConnectionType connectionType,
            Action<RdpSessionViewModel, string> remoteClipboardTextReceived,
            Action<RdpSessionViewModel, string[]> remoteClipboardFilesReceived)
        {
            throw new NotSupportedException("Sessions are created directly in these tests.");
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string Description => "Fake";

        public Task<string?> GetSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string secret) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    }
}

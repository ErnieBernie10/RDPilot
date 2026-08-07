using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

/// <summary>
/// Covers how the host's render scaling reaches a session. The remote DPI is locked at connect
/// time, so anything that delays the scale past the first <c>Create</c> call is not a transient
/// glitch - it leaves that session at the wrong DPI for its whole lifetime.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpViewportScalingTests
{
    [Fact]
    public async Task ConnectAfterAnUnmeasurableViewport_StillUsesTheHostRenderScaling()
    {
        // The viewport is collapsed until a session is live, so this is the state every first
        // connect starts from: a real render scaling but nothing to measure.
        var (vm, factory) = CreateViewModel();
        var presenter = CreatePresenter(vm, viewportSize: new Size(0, 0));

        presenter.QueueViewportResolutionUpdate(1.5, isMinimized: false);
        await ConnectAsync(vm);

        Assert.Equal(1.5, factory.RenderScaling);
    }

    [Fact]
    public async Task ConnectWithoutAnyScaleReport_FallsBackToUnscaled()
    {
        var (vm, factory) = CreateViewModel();

        await ConnectAsync(vm);

        Assert.Equal(1.0, factory.RenderScaling);
    }

    [Fact]
    public async Task MinimizedWindow_DoesNotOverwriteTheKnownScale()
    {
        // RenderScaling is unreliable while minimized; the last known good value must survive.
        var (vm, factory) = CreateViewModel();
        var presenter = CreatePresenter(vm, viewportSize: new Size(0, 0));

        presenter.QueueViewportResolutionUpdate(1.5, isMinimized: false);
        presenter.QueueViewportResolutionUpdate(1.0, isMinimized: true);
        await ConnectAsync(vm);

        Assert.Equal(1.5, factory.RenderScaling);
    }

    [Fact]
    public async Task MeasurableViewportBeforeConnect_SetsTheConnectTimeResolution()
    {
        // The measured container stays visible behind the splash, so the first session connects at
        // the real viewport size rather than the 1280x720 default.
        var (vm, factory) = CreateViewModel();
        var scheduled = new List<Action>();
        var presenter = CreatePresenter(vm, new Size(1600, 900), post: scheduled.Add);

        presenter.QueueViewportResolutionUpdate(1.5, isMinimized: false);
        await WaitForScheduledUpdateAsync(scheduled);
        await ConnectAsync(vm);

        Assert.Equal(2400, factory.Width);
        Assert.Equal(1350, factory.Height);
        Assert.Equal(1.5, factory.RenderScaling);
    }

    private static async Task WaitForScheduledUpdateAsync(List<Action> scheduled)
    {
        // The scheduler debounces on a real timer, so poll rather than guess a single delay.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (scheduled.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.NotEmpty(scheduled);
        foreach (var action in scheduled)
        {
            action();
        }
    }

    private static RdpViewportPresenter CreatePresenter(
        MainWindowViewModel vm,
        Size viewportSize,
        Action<Action>? post = null)
    {
        return new RdpViewportPresenter(
            () => vm,
            () => null,
            _ => [],
            () => viewportSize,
            () => { },
            _ => null,
            new ViewportResolutionService(),
            new ClipboardSyncService(),
            new ViewportResolutionUpdateScheduler(
                post,
                quietDelay: TimeSpan.FromMilliseconds(10),
                minimumInterval: TimeSpan.FromMilliseconds(10)),
            new PointerMoveScheduler());
    }

    private static (MainWindowViewModel ViewModel, CapturingSessionFactory Factory) CreateViewModel()
    {
        var factory = new CapturingSessionFactory();
        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), factory);
        return (vm, factory);
    }

    private static async Task ConnectAsync(MainWindowViewModel vm)
    {
        var connection = new SavedConnection
        {
            Name = "Scaling Test",
            Host = "scaling-test.example.local",
            Username = "user"
        };

        vm.Connections.Add(connection);
        vm.SelectedConnection = connection;
        await vm.ConnectCommand.ExecuteAsync(null);
    }

    private sealed class CapturingSessionFactory : IRdpSessionFactory
    {
        public double RenderScaling { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

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
            RenderScaling = renderScaling;
            Width = width;
            Height = height;
            return new RdpSessionViewModel(connection, RdpSessionStatus.Connected);
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

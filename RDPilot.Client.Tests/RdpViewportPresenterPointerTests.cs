using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpViewportPresenterPointerTests : IDisposable
{
    private const ushort Move = 0x0800;
    private readonly List<IDisposable> _owned = [];
    private readonly Queue<Action> _posted = new();

    [Theory]
    [InlineData("press", (ushort)0x9000)]
    [InlineData("release", (ushort)0x1000)]
    [InlineData("wheel", (ushort)0x0278)]
    public void ButtonAndWheelEvents_FollowThePendingMoveInNativeOrder(string kind, ushort expectedFlags)
    {
        var (presenter, mouseEvents) = CreatePresenter();

        presenter.HandlePointerMoved(10, 20);
        presenter.HandlePointerMoved(30, 40);
        switch (kind)
        {
            case "press":
                presenter.HandlePointerPressed(30, 40, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed));
                break;
            case "release":
                presenter.HandlePointerReleased(30, 40, PointerUpdateKind.LeftButtonReleased);
                break;
            case "wheel":
                presenter.HandlePointerWheelChanged(30, 40, new Vector(0, 1));
                break;
        }
        RunPosted();

        Assert.True(mouseEvents.Count >= 2);
        Assert.Equal((Move, (ushort)30, (ushort)40), mouseEvents[^2]);
        Assert.Equal((expectedFlags, (ushort)30, (ushort)40), mouseEvents[^1]);
    }

    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action)) action();
    }

    private (RdpViewportPresenter Presenter, List<(ushort Flags, ushort X, ushort Y)> MouseEvents) CreatePresenter()
    {
        var context = new SessionTestContext("Pointer Test");
        _owned.Add(context);

        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new NoSessionFactory())
        {
            SelectedSession = context.ViewModel
        };

        var presenter = AvaloniaTestEnvironment.RunOnUiThread(() => new RdpViewportPresenter(
            () => vm,
            () => new Size(1280, 720),
            () => { },
            new ViewportResolutionService(),
            new ViewportResolutionUpdateScheduler(),
            new PointerInputSender(() => vm.SelectedSession, _posted.Enqueue, TimeSpan.FromSeconds(10))));

        return (presenter, context.Native.MouseEvents);
    }

    public void Dispose() => AvaloniaTestEnvironment.RunOnUiThread(() =>
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    });

    private sealed class NoSessionFactory : IRdpSessionFactory
    {
        public RdpSessionViewModel Create(RdpSessionStartRequest request)
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

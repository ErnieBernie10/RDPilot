using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using RDPilot.Client;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

/// <summary>
/// Covers the grabbed-keyboard input path. The presenter takes all of its dependencies as
/// delegates, so this needs no headless Avalonia session.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpViewportPresenterKeyboardGrabTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    [Fact]
    public void HandleGrabbedKey_ForwardsScancodeWithExtendedAndReleaseFlags()
    {
        var (presenter, keyboardEvents, _) = CreatePresenter();

        presenter.HandleGrabbedKey(0x5B, extended: true, isUp: false);
        presenter.HandleGrabbedKey(0x5B, extended: true, isUp: true);
        presenter.HandleGrabbedKey(0x0F, extended: false, isUp: false);

        Assert.Equal(
            [
                ((ushort)0x0100, (ushort)0x5B),
                ((ushort)0x8100, (ushort)0x5B),
                ((ushort)0x0000, (ushort)0x0F)
            ],
            keyboardEvents);
    }

    [Fact]
    public void ReleaseGrabbedKeys_ReleasesOnlyKeysStillHeld()
    {
        var (presenter, keyboardEvents, _) = CreatePresenter();
        presenter.HandleGrabbedKey(0x1D, extended: false, isUp: false);
        presenter.HandleGrabbedKey(0x38, extended: false, isUp: false);
        presenter.HandleGrabbedKey(0x38, extended: false, isUp: true);
        keyboardEvents.Clear();

        presenter.ReleaseGrabbedKeys();

        Assert.Equal(((ushort)0x8000, (ushort)0x1D), Assert.Single(keyboardEvents));
    }

    [Fact]
    public void ReleaseGrabbedKeys_IsIdempotent()
    {
        var (presenter, keyboardEvents, _) = CreatePresenter();
        presenter.HandleGrabbedKey(0x1D, extended: false, isUp: false);
        presenter.ReleaseGrabbedKeys();
        keyboardEvents.Clear();

        presenter.ReleaseGrabbedKeys();

        Assert.Empty(keyboardEvents);
    }

    [Fact]
    public void SetKeyboardGrabActive_EngagingReleasesKeysHeldOnTheAvaloniaPath()
    {
        var (presenter, keyboardEvents, rdpImage) = CreatePresenter();
        Assert.True(presenter.HandleKeyDown(rdpImage, rdpImage, Key.LeftCtrl));
        keyboardEvents.Clear();

        presenter.SetKeyboardGrabActive(true);

        Assert.Equal(((ushort)0x8000, (ushort)0x1D), Assert.Single(keyboardEvents));
    }

    [Fact]
    public void SetKeyboardGrabActive_DisengagingReleasesGrabbedKeys()
    {
        var (presenter, keyboardEvents, _) = CreatePresenter();
        presenter.SetKeyboardGrabActive(true);
        presenter.HandleGrabbedKey(0x38, extended: false, isUp: false);
        keyboardEvents.Clear();

        presenter.SetKeyboardGrabActive(false);

        Assert.Equal(((ushort)0x8000, (ushort)0x38), Assert.Single(keyboardEvents));
    }

    [Fact]
    public void HandleKeyDown_WhileGrabbed_IsIgnoredSoKeysAreNeverSentTwice()
    {
        var (presenter, keyboardEvents, rdpImage) = CreatePresenter();
        presenter.SetKeyboardGrabActive(true);

        var handled = presenter.HandleKeyDown(rdpImage, rdpImage, Key.A);

        Assert.False(handled);
        Assert.Empty(keyboardEvents);
    }

    [Fact]
    public void HandleGrabbedKey_ZeroScancode_IsIgnored()
    {
        var (presenter, keyboardEvents, _) = CreatePresenter();

        Assert.False(presenter.HandleGrabbedKey(0, extended: false, isUp: false));
        Assert.Empty(keyboardEvents);
    }

    private (RdpViewportPresenter Presenter, List<(ushort Flags, ushort Code)> KeyboardEvents, object RdpImage) CreatePresenter()
    {
        var context = new SessionTestContext("Grab Test");
        _owned.Add(context);
        var session = context.ViewModel;
        var nativeSession = context.Native;

        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new NoSessionFactory())
        {
            SelectedSession = session
        };

        var presenter = AvaloniaTestEnvironment.RunOnUiThread(() => new RdpViewportPresenter(
            () => vm,
            () => null,
            _ => Task.FromResult<Avalonia.Platform.Storage.IStorageItem[]>([]),
            () => new Size(1280, 720),
            () => { },
            _ => null,
            new ViewportResolutionService(),
            new ClipboardSyncService(),
            new ViewportResolutionUpdateScheduler(),
            new PointerMoveScheduler()));
        _owned.Add(presenter);

        return (presenter, nativeSession.KeyboardEvents, new object());
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

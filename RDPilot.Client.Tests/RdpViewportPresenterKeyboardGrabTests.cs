using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
public sealed class RdpViewportPresenterKeyboardGrabTests
{
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

    private static (RdpViewportPresenter Presenter, List<(ushort Flags, ushort Code)> KeyboardEvents, object RdpImage) CreatePresenter()
    {
        var connection = new SavedConnection
        {
            Name = "Grab Test",
            Host = "grab-test.example.local",
            Username = "user"
        };

        var session = new RdpSessionViewModel(connection, RdpSessionStatus.Connected);
        var nativeSession = new RecordingNativeSession(new IntPtr(0xBEEF));
        SetField(session, "_nativeSession", nativeSession);
        SetField(session, "_handle", nativeSession.Handle);

        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new NoSessionFactory())
        {
            SelectedSession = session
        };

        var presenter = new RdpViewportPresenter(
            () => vm,
            () => null,
            _ => [],
            () => new Size(1280, 720),
            () => { },
            _ => null,
            new ViewportResolutionService(),
            new ClipboardSyncService(),
            new ViewportResolutionUpdateScheduler(),
            new PointerMoveScheduler());

        return (presenter, nativeSession.KeyboardEvents, new object());
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private sealed class RecordingNativeSession(IntPtr handle) : INativeRdpSession
    {
        public IntPtr Handle { get; } = handle;
        public List<(ushort Flags, ushort Code)> KeyboardEvents { get; } = [];

        public void SendKeyboardEvent(ushort flags, ushort code) => KeyboardEvents.Add((flags, code));

        public void Disconnect() { }
        public void Free() { }
        public void UpdateResolution(int width, int height, uint dpiScalePercent) { }
        public void SendMouseEvent(ushort flags, ushort x, ushort y) { }
        public void SetLocalClipboardText(string? text) { }
        public void SetLocalClipboardFiles(string[] filePaths) { }
        public void SetLocalClipboardBitmap(IntPtr bitmapData, nint bitmapDataSize, uint width, uint height) { }
        public void RequestFullFrame() { }

        public bool Present(
            IntPtr dest,
            int destStride,
            int destWidth,
            int destHeight,
            out int dirtyX,
            out int dirtyY,
            out int dirtyWidth,
            out int dirtyHeight,
            out int fbWidth,
            out int fbHeight)
        {
            dirtyX = dirtyY = dirtyWidth = dirtyHeight = fbWidth = fbHeight = 0;
            return false;
        }
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

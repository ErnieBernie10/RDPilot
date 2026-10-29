using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionRuntimeTests
{
    private static readonly IntPtr SessionHandle = new(0x4321);

    [Fact]
    public void Start_ReceivesStatusAndCertificateDuringConnectBeforeHandleIsAssigned()
    {
        RdpSessionStatus? status = null;
        var certificateDecision = -1;
        using var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.TrustOnce,
            (_, callbacks) =>
            {
                callbacks.Status(SessionHandle, 1, 0, IntPtr.Zero, IntPtr.Zero);
                certificateDecision = callbacks.Certificate(SessionHandle, IntPtr.Zero, 3389, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return new FakeNativeSession(SessionHandle);
            });
        runtime.StatusReceived += (value, error) =>
        {
            status = value;
            Assert.Null(error);
        };

        Assert.Null(runtime.Start(CreateRequest()));

        Assert.Equal(RdpSessionStatus.Connected, status);
        Assert.Equal((int)CertificateTrustDecision.TrustOnce, certificateDecision);
        Assert.True(runtime.HasHandle);
    }

    [Fact]
    public void Callbacks_DecodeUtf8AndIgnoreUnrelatedHandle()
    {
        NativeSessionCallbacks? callbacks = null;
        var texts = new List<string>();
        var files = new List<RemoteFileOffer>();
        var errors = new List<RdpSessionError?>();
        using var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject,
            (_, received) =>
            {
                callbacks = received;
                return new FakeNativeSession(SessionHandle);
            });
        runtime.ClipboardTextReceived += texts.Add;
        runtime.ClipboardFilesReceived += files.Add;
        runtime.StatusReceived += (_, error) => errors.Add(error);
        runtime.Start(CreateRequest());

        var text = Marshal.StringToCoTaskMemUTF8("remote π");
        var descriptor = TestFileDescriptors.Build(("file π.txt", 4, false));
        var descriptorPointer = Marshal.AllocCoTaskMem(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            callbacks!.ClipboardText(new IntPtr(0x9999), text);
            callbacks.ClipboardText(SessionHandle, text);
            callbacks.ClipboardFiles(new IntPtr(0x9999), 7, descriptorPointer, descriptor.Length);
            callbacks.ClipboardFiles(SessionHandle, 7, descriptorPointer, descriptor.Length);
            callbacks.Status(SessionHandle, 2, 77, IntPtr.Zero, IntPtr.Zero);

            Assert.Equal(["remote π"], texts);
            var offer = Assert.Single(files);
            Assert.Equal(7u, offer.Files.Generation);
            Assert.Equal("file π.txt", Assert.Single(offer.Files.Entries).RelativePath);
            Assert.Same(runtime, offer.Source);
            Assert.Equal(77u, Assert.Single(errors)!.NativeCode);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
            Marshal.FreeCoTaskMem(descriptorPointer);
        }
    }

    [Fact]
    public void StatusCallback_MapsNativeCodesAndIgnoresUnknownOnes()
    {
        NativeSessionCallbacks? callbacks = null;
        var statuses = new List<RdpSessionStatus>();
        using var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject,
            (_, received) =>
            {
                callbacks = received;
                return new FakeNativeSession(SessionHandle);
            });
        runtime.StatusReceived += (status, _) => statuses.Add(status);
        runtime.Start(CreateRequest());

        foreach (var code in new[] { 1, 0, 3, 2, 9 }) callbacks!.Status(SessionHandle, code, 0, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal([RdpSessionStatus.Connected, RdpSessionStatus.Disconnected, RdpSessionStatus.Failed], statuses);
    }

    [Fact]
    public void Operations_ReachNativeOnlyWhileSessionIsLive()
    {
        var native = new RecordingNativeSession(SessionHandle);
        var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject, (_, _) => native);

        Assert.False(runtime.Send(new KeyInput(0, 0x1E)));
        runtime.Start(CreateRequest());
        Assert.True(runtime.Send(new KeyInput(0, 0x1E)));
        Assert.True(runtime.Send(new PointerInput(0x0800, 3, 4)));
        runtime.OfferBitmap([1, 2, 3], 1, 1);
        runtime.OfferBitmap([], 1, 1);
        runtime.Resize(1601, 901);

        runtime.Dispose();
        Assert.False(runtime.Send(new KeyInput(0, 0x1F)));
        Assert.False(runtime.Send(new PointerInput(0x0800, 5, 6)));
        runtime.OfferText("late");
        runtime.Resize(800, 600);
        runtime.RequestFullFrame();
        Assert.Equal(default, runtime.PresentInto(IntPtr.Zero, 0, 0, 0));

        Assert.Equal([((ushort)0, (ushort)0x1E)], native.KeyboardEvents);
        Assert.Equal([((ushort)0x0800, (ushort)3, (ushort)4)], native.MouseEvents);
        Assert.Equal([1, 2, 3], Assert.Single(native.ClipboardBitmaps).Data);
        Assert.Equal((1600, 901), Assert.Single(native.ResolutionUpdates));
        Assert.Empty(native.ClipboardTexts);
        Assert.Equal(0, native.FullFrameCount);
    }

    [Fact]
    public void Dispose_FreesOnceAndRejectsLateCallbacks()
    {
        NativeSessionCallbacks? callbacks = null;
        var native = new FakeNativeSession(SessionHandle);
        var received = 0;
        var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.TrustAlways,
            (_, registered) =>
            {
                callbacks = registered;
                return native;
            });
        runtime.FrameReceived += (_, _) => received++;
        runtime.Start(CreateRequest());

        runtime.Dispose();
        runtime.Dispose();
        callbacks!.Frame(SessionHandle, IntPtr.Zero, 1, 1, 0, 0, 1, 1, 4);
        var decision = callbacks.Certificate(SessionHandle, IntPtr.Zero, 3389, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal(1, native.FreeCount);
        Assert.Equal(0, received);
        Assert.Equal((int)CertificateTrustDecision.Reject, decision);
        Assert.False(runtime.HasHandle);
    }

    [Fact]
    public void Start_AfterStartOrDispose_Throws()
    {
        using var started = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject,
            (_, _) => new FakeNativeSession(SessionHandle));
        started.Start(CreateRequest());
        Assert.Throws<InvalidOperationException>(() => started.Start(CreateRequest()));

        using var disposed = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject);
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => disposed.Start(CreateRequest()));
    }

    [Fact]
    public async Task Dispose_WhileConnectorIsRunning_FreesReturnedHandleInsteadOfPublishingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<INativeRdpSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeNativeSession(SessionHandle);
        using var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject,
            (_, _) =>
            {
                entered.SetResult();
                // Deliberately block the fake synchronous connector to control the interleaving.
                return release.Task.GetAwaiter().GetResult();
            });
        var start = Task.Run(() => runtime.Start(CreateRequest()));
        await entered.Task;
        runtime.Dispose();
        release.SetResult(native);

        Assert.Equal("WRAPPER_SESSION_CREATE_FAILED", (await start)?.NativeName);
        Assert.Equal(1, native.FreeCount);
        Assert.False(runtime.HasHandle);
    }

    [Fact]
    public void ViewModel_StatusDeliveredDuringConnect_IsAppliedAfterNativeHandleIsKnown()
    {
        AvaloniaTestEnvironment.EnsureInitialized();
        var session = AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var runtime = new RdpSessionRuntime("saved.example", _ => CertificateTrustDecision.Reject,
                (_, callbacks) =>
                {
                    callbacks.Status(SessionHandle, 1, 0, IntPtr.Zero, IntPtr.Zero);
                    return new FakeNativeSession(SessionHandle);
                });
            var request = CreateRequest();
            var viewModel = new RdpSessionViewModel(request, runtime);
            viewModel.Start(request);
            return viewModel;
        });
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Equal(RdpSessionStatus.Connected, session.Status);
        AvaloniaTestEnvironment.RunOnUiThread(session.Dispose);
    }

    private static RdpSessionStartRequest CreateRequest() => new(
        new SavedConnection { Host = "saved.example" }, "", "", 1280, 720, 1,
        new RdpEffectiveQualitySettings(16, false, false, false, false, false, true, true, RdpConnectionType.Wan));

    private sealed class FakeNativeSession(IntPtr handle) : INativeRdpSession
    {
        public IntPtr Handle { get; } = handle;
        public int FreeCount { get; private set; }
        public void Free() => FreeCount++;
        public void Disconnect() { }
        public void UpdateResolution(int width, int height) { }
        public void SendMouseEvent(ushort flags, ushort x, ushort y) { }
        public void SendKeyboardEvent(ushort flags, ushort code) { }
        public void SetLocalClipboardText(string? text) { }
        public void SetLocalClipboardFiles(string[] paths) { }
        public void SetLocalClipboardBitmap(byte[] data, uint width, uint height) { }
        public bool RequestRemoteFileContents(uint generation, uint streamId, uint listIndex, bool sizeOnly, ulong offset, uint size) => false;
        public void RequestFullFrame() { }
        public FramePresentResult Present(IntPtr dest, int destStride, int destWidth, int destHeight) => default;
        public bool CopyCursorImage(uint cursorId, IntPtr dest, int stride, int width, int height) => false;
    }
}

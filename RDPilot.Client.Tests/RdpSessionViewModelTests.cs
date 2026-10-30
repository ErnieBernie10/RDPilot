using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RDPilot.Client.Models;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionViewModelTests
{
    [Fact]
    public void Constructor_DoesNotStartNativeConnection()
    {
        using var context = new SessionTestContext(start: false);
        Assert.Null(context.Callbacks);
        Assert.False(context.Runtime.HasHandle);

        AvaloniaTestEnvironment.RunOnUiThread(() => context.ViewModel.Start(context.Request));

        Assert.NotNull(context.Callbacks);
        Assert.True(context.Runtime.HasHandle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Start_NativeLoadFailure_ReportsFailureWithoutLeakingResources(int failureKind)
    {
        Exception failure = failureKind switch
        {
            0 => new DllNotFoundException("Wrapper unavailable"),
            1 => new EntryPointNotFoundException("Entry point unavailable"),
            _ => new BadImageFormatException("Wrapper architecture mismatch")
        };
        using var context = new SessionTestContext(duringConnect: _ => throw failure, start: false);

        AvaloniaTestEnvironment.RunOnUiThread(() => context.ViewModel.Start(context.Request));

        Assert.Equal(RdpSessionStatus.Failed, context.ViewModel.Status);
        Assert.Equal("WRAPPER_NATIVE_LOAD_FAILED", context.ViewModel.LastError!.NativeName);
        Assert.False(context.Runtime.HasHandle);
        Assert.Equal(0, context.Native.FreeCount);
    }

    [Fact]
    public void Start_ConnectorReturnsZeroHandle_ReportsCreationFailure()
    {
        using var context = new SessionTestContext(start: false, handle: IntPtr.Zero);

        AvaloniaTestEnvironment.RunOnUiThread(() => context.ViewModel.Start(context.Request));

        Assert.Equal(RdpSessionStatus.Failed, context.ViewModel.Status);
        Assert.Equal("WRAPPER_SESSION_CREATE_FAILED", context.ViewModel.LastError!.NativeName);
        Assert.False(context.Runtime.HasHandle);
    }

    [Fact]
    public void NativeStatusCallback_MatchingHandle_TransitionsToFailedWithMappedError()
    {
        using var context = new SessionTestContext();
        var errorName = Marshal.StringToCoTaskMemUTF8("FREERDP_ERROR_CONNECT_FAILED");
        try
        {
            AvaloniaTestEnvironment.RunOnUiThread(() => context.Callbacks!.Status(context.Native.Handle, 2, 77, errorName, IntPtr.Zero));
        }
        finally { Marshal.FreeCoTaskMem(errorName); }

        var session = context.ViewModel;
        Assert.Equal(RdpSessionStatus.Failed, session.Status);
        Assert.False(session.IsConnected);
        Assert.False(session.CanDisconnect);
        Assert.True(session.CanReconnect);
        Assert.Equal(77u, session.LastError!.NativeCode);
        Assert.Equal("FREERDP_ERROR_CONNECT_FAILED", session.LastError.NativeName);
        Assert.Equal("Connection transport failed or timed out.", session.ErrorText);
        Assert.Equal(RdpSessionErrorKind.TimeoutOrTransport, session.LastError.Kind);
    }

    [Fact]
    public void RemoteClipboardCallback_IgnoresStaleHandleAndRoutesMatchingUtf8Text()
    {
        using var context = new SessionTestContext();
        var text = Marshal.StringToCoTaskMemUTF8("remote π clipboard");
        try
        {
            context.Callbacks!.ClipboardText(new IntPtr(0x9999), text);
            context.Callbacks.ClipboardText(context.Native.Handle, text);
        }
        finally { Marshal.FreeCoTaskMem(text); }
        Assert.Equal(["remote π clipboard"], context.ClipboardTexts);
    }

    [Fact]
    public void RemoteClipboardFilesCallback_IgnoresStaleHandleAndRoutesMatchingFileList()
    {
        using var context = new SessionTestContext();
        var descriptor = TestFileDescriptors.Build(("alpha.txt", 1, false), ("folder", null, true), ("folder\\beta.bin", 2, false));
        var pointer = Marshal.AllocCoTaskMem(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, pointer, descriptor.Length);
            context.Callbacks!.ClipboardFiles(new IntPtr(0x9999), 3, pointer, descriptor.Length);
            context.Callbacks.ClipboardFiles(context.Native.Handle, 3, pointer, descriptor.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
        var offer = Assert.Single(context.ClipboardFiles);
        Assert.Equal(["alpha.txt", "folder", "folder\\beta.bin"], offer.Files.Entries.Select(entry => entry.RelativePath));
        Assert.True(offer.Files.Entries[1].IsDirectory);
    }

    [Fact]
    public void CertificateDecisionCallback_RejectsStaleHandleAndBuildsPromptForMatchingHandle()
    {
        RdpCertificatePrompt? prompt = null;
        using var context = new SessionTestContext(decideCertificate: value => { prompt = value; return CertificateTrustDecision.TrustOnce; });
        var commonName = Marshal.StringToCoTaskMemUTF8("remote.example");
        var fingerprint = Marshal.StringToCoTaskMemUTF8("AB:CD");
        try
        {
            Assert.Equal((int)CertificateTrustDecision.Reject, context.Callbacks!.Certificate(new IntPtr(0x9999), IntPtr.Zero,
                3389, commonName, IntPtr.Zero, IntPtr.Zero, fingerprint, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            Assert.Null(prompt);
            Assert.Equal((int)CertificateTrustDecision.TrustOnce, context.Callbacks.Certificate(context.Native.Handle, IntPtr.Zero,
                3389, commonName, commonName, commonName, fingerprint, 1, commonName, commonName, fingerprint));
        }
        finally { Marshal.FreeCoTaskMem(commonName); Marshal.FreeCoTaskMem(fingerprint); }

        Assert.NotNull(prompt);
        Assert.Equal(context.Request.Connection.Host, prompt.Host);
        Assert.Equal((ushort)3389, prompt.Port);
        Assert.Equal("remote.example", prompt.CommonName);
        Assert.Equal("remote.example", prompt.Subject);
        Assert.Equal("remote.example", prompt.Issuer);
        Assert.Equal("AB:CD", prompt.Fingerprint);
        Assert.True(prompt.IsChanged);
        Assert.Equal("remote.example", prompt.PreviousSubject);
        Assert.Equal("remote.example", prompt.PreviousIssuer);
        Assert.Equal("AB:CD", prompt.PreviousFingerprint);
    }

    [Fact]
    public void NativeStatusCallback_DuringNativeInitialization_AcceptsFirstSessionCallback()
    {
        using var context = new SessionTestContext(duringConnect: callbacks =>
            callbacks.Status(new IntPtr(0x1234), 1, 0, IntPtr.Zero, IntPtr.Zero));
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();
        Assert.Equal(RdpSessionStatus.Connected, context.ViewModel.Status);
    }

    [Fact]
    public void NativeStatusCallback_DisposedBeforeDispatcherDrain_DropsPendingStateChange()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            context.Callbacks!.Status(context.Native.Handle, 1, 0, IntPtr.Zero, IntPtr.Zero);
            context.ViewModel.Dispose();
        });
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();
        Assert.Equal(RdpSessionStatus.Connecting, context.ViewModel.Status);
        Assert.False(context.ViewModel.IsConnected);
    }

    [Fact]
    public void Callbacks_DisposedSession_DropClipboardAndRejectCertificate()
    {
        using var context = new SessionTestContext(decideCertificate: _ => throw new InvalidOperationException("Must not prompt"));
        context.Dispose();
        context.Callbacks!.ClipboardText(context.Native.Handle, IntPtr.Zero);
        context.Callbacks.ClipboardFiles(context.Native.Handle, 1, IntPtr.Zero, 1);
        var decision = context.Callbacks.Certificate(context.Native.Handle, IntPtr.Zero, 3389, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.Empty(context.ClipboardTexts);
        Assert.Empty(context.ClipboardFiles);
        Assert.Equal((int)CertificateTrustDecision.Reject, decision);
    }

    [Fact]
    public async Task DisconnectAsync_ActiveNativeSession_DisconnectsAndTransitionsDisconnected()
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(context.ViewModel.DisconnectAsync);
        Assert.Equal(1, context.Native.DisconnectCount);
        Assert.Equal(RdpSessionStatus.Disconnected, context.ViewModel.Status);
        Assert.Null(context.ViewModel.LastError);
    }

    [Fact]
    public void UpdateResolution_ForwardsNormalizedDimensionsAndConnectTimeDpiScale()
    {
        using var context = new SessionTestContext(scaling: 1.5);
        AvaloniaTestEnvironment.RunOnUiThread(() => context.ViewModel.UpdateResolution(1601, 901, 2));
        Assert.Equal((1600, 901, 150u), Assert.Single(context.Native.ResolutionUpdates));
    }

    [Fact]
    public void UpdateResolution_RenderScalingChanges_RefreshesDisplayDimensions()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            using var bitmap = new WriteableBitmap(new PixelSize(1000, 500), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            context.ViewModel.Screen = bitmap;
            var changed = new List<string?>();
            context.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            context.ViewModel.UpdateResolution(2000, 1000, 2);
            Assert.Equal(500, context.ViewModel.DisplayWidth);
            Assert.Equal(250, context.ViewModel.DisplayHeight);
            Assert.Contains(nameof(context.ViewModel.DisplayWidth), changed);
            Assert.Contains(nameof(context.ViewModel.DisplayHeight), changed);
            context.ViewModel.Screen = null;
        });
    }

    [Fact]
    public void SendCtrlAltDel_SendsBalancedDownUpSequence()
    {
        using var context = new SessionTestContext();
        context.ViewModel.SendCtrlAltDel();
        Assert.Equal([
            ((ushort)0, (ushort)0x1D), ((ushort)0, (ushort)0x38), ((ushort)0x100, (ushort)0x53),
            ((ushort)0x8100, (ushort)0x53), ((ushort)0x8000, (ushort)0x38), ((ushort)0x8000, (ushort)0x1D)
        ], context.Native.KeyboardEvents);
    }

    [Fact]
    public void Status_LeavingConnected_ClearsKeyboardGrab()
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            context.ViewModel.IsKeyboardGrabbed = true;
            context.Callbacks!.Status(context.Native.Handle, 3, 0, IntPtr.Zero, IntPtr.Zero);
        });
        Assert.False(context.ViewModel.IsKeyboardGrabbed);
    }

    [Fact]
    public void SetLocalClipboardFiles_ForwardsAndClearsFileList()
    {
        using var context = new SessionTestContext();
        context.ViewModel.SetLocalClipboardFiles(["alpha.txt", "beta.bin"]);
        context.ViewModel.SetLocalClipboardFiles([]);
        Assert.Equal(["alpha.txt", "beta.bin"], context.Native.ClipboardFiles[0]);
        Assert.Empty(context.Native.ClipboardFiles[1]);
    }

    [Fact]
    public void Dispose_FreesNativeSessionOnceAndStopsInput()
    {
        using var context = new SessionTestContext();
        context.Dispose();
        context.Dispose();
        context.ViewModel.Send(new KeyInput(0, 10));
        Assert.Equal(1, context.Native.FreeCount);
        Assert.False(context.Runtime.HasHandle);
        Assert.Empty(context.Native.KeyboardEvents);
    }

    [Fact]
    public void ResumePresentation_RequestsFullNativeFrame()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() => { context.ViewModel.SuspendPresentation(); context.ViewModel.ResumePresentation(); });
        Assert.Equal(1, context.Native.FullFrameCount);
    }

    [Fact]
    public void NativeCursorCallback_MatchingHandle_AppliesHiddenCursor()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() => context.Callbacks!.Cursor(context.Native.Handle, 0, 0, 0, 0, 0, 0));
        Assert.NotNull(context.ViewModel.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_StaleHandle_IsIgnored()
    {
        using var context = new SessionTestContext();
        context.Callbacks!.Cursor(new IntPtr(0x9999), 0, 0, 0, 0, 0, 0);
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();
        Assert.Null(context.ViewModel.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_DisposedBeforeDispatcherDrain_DropsPendingCursor()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            context.Callbacks!.Cursor(context.Native.Handle, 2, 5, 32, 32, 0, 0);
            context.ViewModel.Dispose();
        });
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();
        Assert.Empty(context.Native.CursorRequests);
        Assert.Null(context.ViewModel.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_BurstOfChanges_AppliesOnlyLatest()
    {
        using var context = new SessionTestContext();
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            context.Callbacks!.Cursor(context.Native.Handle, 2, 11, 32, 32, 0, 0);
            context.Callbacks.Cursor(context.Native.Handle, 2, 12, 32, 32, 0, 0);
            context.Callbacks.Cursor(context.Native.Handle, 2, 13, 32, 32, 0, 0);
        });
        Assert.Equal([13u], context.Native.CursorRequests);
        Assert.Null(context.ViewModel.RemoteCursor);
    }
}

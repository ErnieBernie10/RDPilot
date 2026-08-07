using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RDPilot.Client;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionViewModelTests
{
    [Fact]
    public void NativeStatusCallback_MatchingHandle_TransitionsToFailedWithMappedError()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Status Test"), RdpSessionStatus.Connecting);
        var handle = new IntPtr(0x1234);
        SetField(session, "_handle", handle);

        using var errorName = Utf8String.Alloc("FREERDP_ERROR_CONNECT_FAILED");

        AvaloniaTestEnvironment.RunOnUiThread(() =>
            InvokePrivate(
                session,
                "OnStatusChanged",
                [typeof(IntPtr), typeof(int), typeof(uint), typeof(IntPtr), typeof(IntPtr)],
                handle,
                2,
                77u,
                errorName.Pointer,
                IntPtr.Zero));

        Assert.Equal(RdpSessionStatus.Failed, session.Status);
        Assert.False(session.IsConnected);
        Assert.False(session.CanDisconnect);
        Assert.True(session.CanReconnect);

        var error = Assert.IsType<RdpSessionError>(session.LastError);
        Assert.Equal(77u, error.NativeCode);
        Assert.Equal("FREERDP_ERROR_CONNECT_FAILED", error.NativeName);
        Assert.Equal("Connection transport failed or timed out.", error.Message);
        Assert.Equal(RdpSessionErrorKind.TimeoutOrTransport, error.Kind);
        Assert.Equal(error.Message, session.ErrorText);
    }

    [Fact]
    public void RemoteClipboardCallback_IgnoresStaleHandleAndRoutesMatchingUtf8Text()
    {
        var session = new RdpSessionViewModel(CreateConnection("Clipboard Test"), RdpSessionStatus.Connected);
        var activeHandle = new IntPtr(0x1234);
        SetField(session, "_handle", activeHandle);

        var deliveries = new List<(RdpSessionViewModel Session, string Text)>();
        SetField(
            session,
            "_remoteClipboardTextReceived",
            (Action<RdpSessionViewModel, string>)((vm, text) => deliveries.Add((vm, text))));

        using var staleText = Utf8String.Alloc("stale");
        InvokePrivate(
            session,
            "OnRemoteClipboardTextReceived",
            [typeof(IntPtr), typeof(IntPtr)],
            new IntPtr(0x9999),
            staleText.Pointer);

        using var activeText = Utf8String.Alloc("remote π clipboard");
        InvokePrivate(
            session,
            "OnRemoteClipboardTextReceived",
            [typeof(IntPtr), typeof(IntPtr)],
            activeHandle,
            activeText.Pointer);

        var delivery = Assert.Single(deliveries);
        Assert.Same(session, delivery.Session);
        Assert.Equal("remote π clipboard", delivery.Text);
    }

    [Fact]
    public void RemoteClipboardFilesCallback_IgnoresStaleHandleAndRoutesMatchingUtf8Paths()
    {
        var session = new RdpSessionViewModel(CreateConnection("Clipboard Files Test"), RdpSessionStatus.Connected);
        var activeHandle = new IntPtr(0x1235);
        SetField(session, "_handle", activeHandle);

        var deliveries = new List<(RdpSessionViewModel Session, string[] Paths)>();
        SetField(
            session,
            "_remoteClipboardFilesReceived",
            (Action<RdpSessionViewModel, string[]>)((vm, paths) => deliveries.Add((vm, paths))));

        using var staleFirst = Utf8String.Alloc("stale-a.txt");
        using var staleSecond = Utf8String.Alloc("stale-b.txt");
        using var stalePaths = Utf8PointerArray.Alloc(staleFirst.Pointer, staleSecond.Pointer);
        InvokePrivate(
            session,
            "OnRemoteClipboardFilesReceived",
            [typeof(IntPtr), typeof(IntPtr), typeof(nint)],
            new IntPtr(0x9999),
            stalePaths.Pointer,
            (nint)2);

        using var activeFirst = Utf8String.Alloc("C:\\Temp\\alpha.txt");
        using var activeSecond = Utf8String.Alloc("/tmp/beta.bin");
        using var activePaths = Utf8PointerArray.Alloc(activeFirst.Pointer, activeSecond.Pointer);
        InvokePrivate(
            session,
            "OnRemoteClipboardFilesReceived",
            [typeof(IntPtr), typeof(IntPtr), typeof(nint)],
            activeHandle,
            activePaths.Pointer,
            (nint)2);

        var delivery = Assert.Single(deliveries);
        Assert.Same(session, delivery.Session);
        Assert.Equal(["C:\\Temp\\alpha.txt", "/tmp/beta.bin"], delivery.Paths);
    }

    [Fact]
    public void CertificateDecisionCallback_RejectsStaleHandleAndBuildsPromptForMatchingHandle()
    {
        var session = new RdpSessionViewModel(CreateConnection("Certificate Test"), RdpSessionStatus.Connecting);
        var activeHandle = new IntPtr(0x2345);
        SetField(session, "_handle", activeHandle);

        RdpCertificatePrompt? capturedPrompt = null;
        SetField(
            session,
            "_certificateTrustDecision",
            (Func<RdpCertificatePrompt, CertificateTrustDecision>)(prompt =>
            {
                capturedPrompt = prompt;
                return CertificateTrustDecision.TrustOnce;
            }));

        using var commonName = Utf8String.Alloc("rdp.example.local");
        using var subject = Utf8String.Alloc("CN=rdp.example.local");
        using var issuer = Utf8String.Alloc("CN=Lab CA");
        using var fingerprint = Utf8String.Alloc("AB:CD:EF");
        using var previousSubject = Utf8String.Alloc("CN=old.example.local");
        using var previousIssuer = Utf8String.Alloc("CN=Old Lab CA");
        using var previousFingerprint = Utf8String.Alloc("00:11:22");

        var staleDecision = InvokePrivate<int>(
            session,
            "OnCertificateDecisionRequested",
            [typeof(IntPtr), typeof(IntPtr), typeof(ushort), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr)],
            new IntPtr(0x9999),
            IntPtr.Zero,
            (ushort)3389,
            commonName.Pointer,
            subject.Pointer,
            issuer.Pointer,
            fingerprint.Pointer,
            1,
            previousSubject.Pointer,
            previousIssuer.Pointer,
            previousFingerprint.Pointer);

        Assert.Equal((int)CertificateTrustDecision.Reject, staleDecision);
        Assert.Null(capturedPrompt);

        var decision = InvokePrivate<int>(
            session,
            "OnCertificateDecisionRequested",
            [typeof(IntPtr), typeof(IntPtr), typeof(ushort), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr)],
            activeHandle,
            IntPtr.Zero,
            (ushort)3389,
            commonName.Pointer,
            subject.Pointer,
            issuer.Pointer,
            fingerprint.Pointer,
            1,
            previousSubject.Pointer,
            previousIssuer.Pointer,
            previousFingerprint.Pointer);

        Assert.Equal((int)CertificateTrustDecision.TrustOnce, decision);
        var prompt = Assert.IsType<RdpCertificatePrompt>(capturedPrompt);
        Assert.Equal(session.Connection.Host, prompt.Host);
        Assert.Equal((ushort)3389, prompt.Port);
        Assert.Equal("rdp.example.local", prompt.CommonName);
        Assert.Equal("CN=rdp.example.local", prompt.Subject);
        Assert.Equal("CN=Lab CA", prompt.Issuer);
        Assert.Equal("AB:CD:EF", prompt.Fingerprint);
        Assert.True(prompt.IsChanged);
        Assert.Equal("CN=old.example.local", prompt.PreviousSubject);
        Assert.Equal("CN=Old Lab CA", prompt.PreviousIssuer);
        Assert.Equal("00:11:22", prompt.PreviousFingerprint);
    }

    [Fact]
    public void CertificateDecisionCallback_DuringNativeInitialization_AcceptsFirstSessionCallback()
    {
        var session = new RdpSessionViewModel(CreateConnection("Certificate Startup"), RdpSessionStatus.Connecting);
        SetField(session, "_initializingNativeSession", 1);

        SetField(
            session,
            "_certificateTrustDecision",
            (Func<RdpCertificatePrompt, CertificateTrustDecision>)(_ => CertificateTrustDecision.TrustOnce));

        using var commonName = Utf8String.Alloc("127.0.0.1");
        using var subject = Utf8String.Alloc("CN=127.0.0.1");
        using var issuer = Utf8String.Alloc("CN=Lab CA");
        using var fingerprint = Utf8String.Alloc("AB:CD:EF");

        var decision = InvokePrivate<int>(
            session,
            "OnCertificateDecisionRequested",
            [typeof(IntPtr), typeof(IntPtr), typeof(ushort), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr)],
            new IntPtr(0x2345),
            IntPtr.Zero,
            (ushort)3390,
            commonName.Pointer,
            subject.Pointer,
            issuer.Pointer,
            fingerprint.Pointer,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        Assert.Equal((int)CertificateTrustDecision.TrustOnce, decision);
    }

    [Fact]
    public void NativeStatusCallback_DuringNativeInitialization_AcceptsFirstSessionCallback()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Status Startup"), RdpSessionStatus.Connecting);
        SetField(session, "_initializingNativeSession", 1);

        AvaloniaTestEnvironment.RunOnUiThread(() =>
            InvokePrivate(
                session,
                "OnStatusChanged",
                [typeof(IntPtr), typeof(int), typeof(uint), typeof(IntPtr), typeof(IntPtr)],
                new IntPtr(0x2345),
                1,
                0u,
                IntPtr.Zero,
                IntPtr.Zero));

        Assert.Equal(RdpSessionStatus.Connected, session.Status);
    }

    [Fact]
    public void NativeStatusCallback_DisposedBeforeDispatcherDrain_DropsPendingStateChange()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Status Dispose Race"), RdpSessionStatus.Connecting);
        var activeHandle = new IntPtr(0x3456);
        SetField(session, "_handle", activeHandle);

        InvokePrivate(
            session,
            "OnStatusChanged",
            [typeof(IntPtr), typeof(int), typeof(uint), typeof(IntPtr), typeof(IntPtr)],
            activeHandle,
            1,
            0u,
            IntPtr.Zero,
            IntPtr.Zero);

        DisposeWithoutNativeFree(session);
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Equal(RdpSessionStatus.Connecting, session.Status);
        Assert.Null(session.LastError);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public void RemoteClipboardCallback_DisposedSession_IgnoresOldHandleDelivery()
    {
        var session = new RdpSessionViewModel(CreateConnection("Clipboard Dispose"), RdpSessionStatus.Connected);
        var oldHandle = new IntPtr(0x3456);
        SetField(session, "_handle", oldHandle);

        var deliveries = new List<(RdpSessionViewModel Session, string Text)>();
        SetField(
            session,
            "_remoteClipboardTextReceived",
            (Action<RdpSessionViewModel, string>)((vm, text) => deliveries.Add((vm, text))));

        DisposeWithoutNativeFree(session);

        using var text = Utf8String.Alloc("post-dispose clipboard");
        InvokePrivate(
            session,
            "OnRemoteClipboardTextReceived",
            [typeof(IntPtr), typeof(IntPtr)],
            oldHandle,
            text.Pointer);

        Assert.Empty(deliveries);
    }

    [Fact]
    public void CertificateDecisionCallback_DisposedSession_RejectsOldHandleWithoutPrompt()
    {
        var session = new RdpSessionViewModel(CreateConnection("Certificate Dispose"), RdpSessionStatus.Connecting);
        var oldHandle = new IntPtr(0x4567);
        SetField(session, "_handle", oldHandle);

        RdpCertificatePrompt? capturedPrompt = null;
        SetField(
            session,
            "_certificateTrustDecision",
            (Func<RdpCertificatePrompt, CertificateTrustDecision>)(prompt =>
            {
                capturedPrompt = prompt;
                return CertificateTrustDecision.TrustAlways;
            }));

        DisposeWithoutNativeFree(session);

        using var commonName = Utf8String.Alloc("rdp.example.local");
        using var subject = Utf8String.Alloc("CN=rdp.example.local");
        using var issuer = Utf8String.Alloc("CN=Lab CA");
        using var fingerprint = Utf8String.Alloc("AB:CD:EF");

        var decision = InvokePrivate<int>(
            session,
            "OnCertificateDecisionRequested",
            [typeof(IntPtr), typeof(IntPtr), typeof(ushort), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr)],
            oldHandle,
            IntPtr.Zero,
            (ushort)3389,
            commonName.Pointer,
            subject.Pointer,
            issuer.Pointer,
            fingerprint.Pointer,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        Assert.Equal((int)CertificateTrustDecision.Reject, decision);
        Assert.Null(capturedPrompt);
    }

    [Fact]
    [SuppressMessage("xUnit", "xUnit1031:Test methods should not use blocking task operations", Justification = "Keeping this Avalonia test class synchronous avoids thread-affinity issues with the shared dispatcher-backed test environment.")]
    public void DisconnectAsync_ActiveNativeSession_DisconnectsAndTransitionsDisconnected()
    {
        var session = new RdpSessionViewModel(CreateConnection("Disconnect Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x5678));
        AttachNativeSession(session, nativeSession);

        session.DisconnectAsync().GetAwaiter().GetResult();

        Assert.Equal(1, nativeSession.DisconnectCallCount);
        Assert.Equal(RdpSessionStatus.Disconnected, session.Status);
        Assert.Null(session.LastError);
    }

    [Fact]
    public void UpdateResolution_ActiveNativeSession_ForwardsDimensionsAndConnectTimeDpiScale()
    {
        var session = new RdpSessionViewModel(CreateConnection("Resolution Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x6789));
        AttachNativeSession(session, nativeSession);

        session.UpdateResolution(1600, 900, renderScaling: 1.4);

        Assert.Equal((1600, 900, 100u), Assert.Single(nativeSession.ResolutionUpdates));
        Assert.Equal(1.4, Assert.IsType<double>(GetField(session, "_renderScaling")));
    }

    [Fact]
    public void UpdateResolution_RenderScalingChanges_RefreshesDisplayDimensions()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            using var session = new RdpSessionViewModel(CreateConnection("Scale Test"), RdpSessionStatus.Connected);
            var nativeSession = new FakeNativeSession(new IntPtr(0x6790));
            AttachNativeSession(session, nativeSession);
            using var bitmap = new WriteableBitmap(
                new PixelSize(1000, 500),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            session.Screen = bitmap;
            var changedProperties = new List<string?>();
            session.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

            session.UpdateResolution(2000, 1000, renderScaling: 2.0);

            Assert.Equal(500, session.DisplayWidth);
            Assert.Equal(250, session.DisplayHeight);
            Assert.Contains(nameof(session.DisplayWidth), changedProperties);
            Assert.Contains(nameof(session.DisplayHeight), changedProperties);
        });
    }

    [Fact]
    public void SendMouseEventScaled_ActiveNativeSession_ScalesCoordinatesAndForwardsFlags()
    {
        var session = new RdpSessionViewModel(CreateConnection("Mouse Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x789A));
        AttachNativeSession(session, nativeSession);
        SetField(session, "_renderScaling", 1.5d);

        session.SendMouseEventScaled(0x8001, 10.4, 20.8);

        Assert.Equal((0x8001, (ushort)15, (ushort)31), Assert.Single(nativeSession.MouseEvents));
    }

    [Fact]
    public void SendCtrlAltDel_ActiveNativeSession_SendsBalancedDownUpSequence()
    {
        var session = new RdpSessionViewModel(CreateConnection("Ctrl Alt Del Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x789B));
        AttachNativeSession(session, nativeSession);

        session.SendCtrlAltDel();

        Assert.Equal(
            [
                ((ushort)0x0000, (ushort)0x1D),
                ((ushort)0x0000, (ushort)0x38),
                ((ushort)0x0100, (ushort)0x53),
                ((ushort)0x8100, (ushort)0x53),
                ((ushort)0x8000, (ushort)0x38),
                ((ushort)0x8000, (ushort)0x1D)
            ],
            nativeSession.KeyboardEvents);
    }

    [Fact]
    public void Status_LeavingConnected_ClearsKeyboardGrab()
    {
        var session = new RdpSessionViewModel(CreateConnection("Grab Status Test"), RdpSessionStatus.Connected)
        {
            IsKeyboardGrabbed = true
        };

        session.SetTestStatus(RdpSessionStatus.Disconnected);

        Assert.False(session.IsKeyboardGrabbed);
    }

    [Fact]
    public void SetLocalClipboardFiles_ActiveNativeSession_ForwardsFileList()
    {
        var session = new RdpSessionViewModel(CreateConnection("Files Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x89AB));
        AttachNativeSession(session, nativeSession);

        session.SetLocalClipboardFiles(["C:\\Temp\\alpha.txt", "/tmp/beta.bin"]);

        Assert.Equal(["C:\\Temp\\alpha.txt", "/tmp/beta.bin"], nativeSession.LocalClipboardFiles);
    }

    [Fact]
    public void SetLocalClipboardFiles_EmptyArray_ClearsExistingFileList()
    {
        var session = new RdpSessionViewModel(CreateConnection("Files Clear Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x89AC));
        nativeSession.LocalClipboardFiles.Add("stale.txt");
        AttachNativeSession(session, nativeSession);

        session.SetLocalClipboardFiles([]);

        Assert.Empty(nativeSession.LocalClipboardFiles);
    }

    [Fact]
    public void Dispose_ActiveNativeSession_FreesNativeSessionOnce()
    {
        var session = new RdpSessionViewModel(CreateConnection("Dispose Native Test"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x9ABC));
        AttachNativeSession(session, nativeSession);

        session.Dispose();

        Assert.Equal(1, nativeSession.FreeCallCount);
        Assert.Equal(IntPtr.Zero, Assert.IsType<IntPtr>(GetField(session, "_handle")));
    }

    [Fact]
    public void ResumePresentation_RequestsFullNativeFrame()
    {
        AvaloniaTestEnvironment.EnsureInitialized();
        var session = new RdpSessionViewModel(CreateConnection("Resume Presentation"), RdpSessionStatus.Connected);
        var nativeSession = new FakeNativeSession(new IntPtr(0x9ABD));
        AttachNativeSession(session, nativeSession);

        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            session.SuspendPresentation();
            session.ResumePresentation();
        });

        Assert.Equal(1, nativeSession.RequestFullFrameCallCount);
        AvaloniaTestEnvironment.RunOnUiThread(session.Dispose);
    }

    [Fact]
    public void NativeCursorCallback_MatchingHandle_AppliesHiddenCursorOnUiThread()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Cursor Hidden"), RdpSessionStatus.Connected);
        var handle = new IntPtr(0x1234);
        SetField(session, "_handle", handle);

        AvaloniaTestEnvironment.RunOnUiThread(() =>
            InvokeCursorCallback(session, handle, kind: 0, cursorId: 0, width: 0, height: 0, hotX: 0, hotY: 0));

        Assert.NotNull(session.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_StaleHandle_IsIgnored()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Cursor Stale"), RdpSessionStatus.Connected);
        SetField(session, "_handle", new IntPtr(0x1234));

        InvokeCursorCallback(session, new IntPtr(0x9999), kind: 0, cursorId: 0, width: 0, height: 0, hotX: 0, hotY: 0);
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Null(session.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_DisposedBeforeDispatcherDrain_DropsPendingCursor()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Cursor Dispose Race"), RdpSessionStatus.Connected);
        var handle = new IntPtr(0x1234);
        SetField(session, "_handle", handle);
        var nativeSession = new FakeNativeSession(handle);
        AttachNativeSession(session, nativeSession);

        // Queue the apply, then dispose before the dispatcher gets to run it.
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            InvokeCursorCallback(session, handle, kind: 2, cursorId: 5, width: 32, height: 32, hotX: 0, hotY: 0);
            DisposeWithoutNativeFree(session);
        });
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Empty(nativeSession.CursorImageRequests);
        Assert.Null(session.RemoteCursor);
    }

    [Fact]
    public void NativeCursorCallback_BurstOfChanges_AppliesOnlyTheLatest()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var session = new RdpSessionViewModel(CreateConnection("Cursor Coalesce"), RdpSessionStatus.Connected);
        var handle = new IntPtr(0x1234);
        SetField(session, "_handle", handle);
        var nativeSession = new FakeNativeSession(handle);
        AttachNativeSession(session, nativeSession);

        // Three bitmap shapes arrive before the dispatcher drains; only the last should be pulled.
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            InvokeCursorCallback(session, handle, kind: 2, cursorId: 11, width: 32, height: 32, hotX: 0, hotY: 0);
            InvokeCursorCallback(session, handle, kind: 2, cursorId: 12, width: 32, height: 32, hotX: 0, hotY: 0);
            InvokeCursorCallback(session, handle, kind: 2, cursorId: 13, width: 32, height: 32, hotX: 0, hotY: 0);
        });

        Assert.Equal([13u], nativeSession.CursorImageRequests);
        // FakeNativeSession reports the copy as failed, so the viewport keeps its current cursor.
        Assert.Null(session.RemoteCursor);
    }

    private static void InvokeCursorCallback(RdpSessionViewModel session, IntPtr handle, int kind, uint cursorId, int width, int height, int hotX, int hotY)
    {
        InvokePrivate(
            session,
            "OnCursorChanged",
            [typeof(IntPtr), typeof(int), typeof(uint), typeof(int), typeof(int), typeof(int), typeof(int)],
            handle,
            kind,
            cursorId,
            width,
            height,
            hotX,
            hotY);
    }

    private static void DisposeWithoutNativeFree(RdpSessionViewModel session)
    {
        SetField(session, "_handle", IntPtr.Zero);
        session.Dispose();
    }

    private static void AttachNativeSession(RdpSessionViewModel session, INativeRdpSession nativeSession)
    {
        SetField(session, "_nativeSession", nativeSession);
        SetField(session, "_handle", nativeSession.Handle);
    }


    private static SavedConnection CreateConnection(string name)
    {
        return new SavedConnection
        {
            Name = name,
            Host = $"{name.ToLowerInvariant().Replace(' ', '-')}.example.local",
            Username = "user"
        };
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static object? GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(target);
    }

    private static void InvokePrivate(object target, string methodName, Type[] parameterTypes, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, parameterTypes, modifiers: null);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static T InvokePrivate<T>(object target, string methodName, Type[] parameterTypes, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, parameterTypes, modifiers: null);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(target, args));
    }

    private sealed class Utf8String : IDisposable
    {
        private Utf8String(IntPtr pointer)
        {
            Pointer = pointer;
        }

        public IntPtr Pointer { get; }

        public static Utf8String Alloc(string value)
        {
            return new Utf8String(Marshal.StringToCoTaskMemUTF8(value));
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    private sealed class Utf8PointerArray : IDisposable
    {
        private Utf8PointerArray(IntPtr pointer)
        {
            Pointer = pointer;
        }

        public IntPtr Pointer { get; }

        public static Utf8PointerArray Alloc(params IntPtr[] pointers)
        {
            var buffer = Marshal.AllocCoTaskMem(pointers.Length * IntPtr.Size);
            for (var i = 0; i < pointers.Length; i++)
            {
                Marshal.WriteIntPtr(buffer, i * IntPtr.Size, pointers[i]);
            }

            return new Utf8PointerArray(buffer);
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    private sealed class FakeNativeSession(IntPtr handle) : INativeRdpSession
    {
        public IntPtr Handle { get; } = handle;
        public int DisconnectCallCount { get; private set; }
        public int FreeCallCount { get; private set; }
        public List<(int Width, int Height, uint DpiScalePercent)> ResolutionUpdates { get; } = [];
        public List<(ushort Flags, ushort X, ushort Y)> MouseEvents { get; } = [];
        public List<(ushort Flags, ushort Code)> KeyboardEvents { get; } = [];
        public List<string> LocalClipboardFiles { get; } = [];
        public int RequestFullFrameCallCount { get; private set; }
        public List<uint> CursorImageRequests { get; } = [];

        public void Disconnect()
        {
            DisconnectCallCount++;
        }

        public void Free()
        {
            FreeCallCount++;
        }

        public void UpdateResolution(int width, int height, uint dpiScalePercent)
        {
            ResolutionUpdates.Add((width, height, dpiScalePercent));
        }

        public void SendMouseEvent(ushort flags, ushort x, ushort y)
        {
            MouseEvents.Add((flags, x, y));
        }

        public void SendKeyboardEvent(ushort flags, ushort code)
        {
            KeyboardEvents.Add((flags, code));
        }

        public void SetLocalClipboardText(string? text)
        {
        }

        public void SetLocalClipboardFiles(string[] filePaths)
        {
            LocalClipboardFiles.Clear();
            LocalClipboardFiles.AddRange(filePaths);
        }

        public void SetLocalClipboardBitmap(IntPtr bitmapData, nint bitmapDataSize, uint width, uint height)
        {
        }

        public void RequestFullFrame()
        {
            RequestFullFrameCallCount++;
        }

        public bool Present(IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight)
        {
            dirtyX = dirtyY = dirtyWidth = dirtyHeight = 0;
            fbWidth = fbHeight = 0;
            return false;
        }

        public bool CopyCursorImage(uint cursorId, IntPtr dest, int destStride, int destWidth, int destHeight)
        {
            CursorImageRequests.Add(cursorId);
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
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

        InvokePrivate(
            session,
            "OnStatusChanged",
            [typeof(IntPtr), typeof(int), typeof(uint), typeof(IntPtr), typeof(IntPtr)],
            handle,
            2,
            77u,
            errorName.Pointer,
            IntPtr.Zero);

        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

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
}

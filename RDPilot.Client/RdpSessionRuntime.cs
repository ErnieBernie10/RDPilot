using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client;

/// <summary>Owns a single native session and the callback delegates rooted for its entire lifetime.</summary>
internal sealed class RdpSessionRuntime : IRemoteFileContentSource, IDisposable
{
    private readonly Func<RdpSessionStartRequest, NativeSessionCallbacks, INativeRdpSession> _connect;
    private readonly Func<RdpCertificatePrompt, CertificateTrustDecision> _decideCertificate;
    private readonly NativeSessionCallbacks _callbacks;
    private readonly object _lifetimeLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]?>> _fileContentsRequests = new();
    private int _nextFileContentsStreamId;
    private INativeRdpSession? _session;
    private IntPtr _handle;
    private int _initializing;
    private int _disposeStarted;
    private bool _started;
    private readonly string _host;

    internal RdpSessionRuntime(string host, Func<RdpCertificatePrompt, CertificateTrustDecision> decideCertificate,
        Func<RdpSessionStartRequest, NativeSessionCallbacks, INativeRdpSession>? connect = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _decideCertificate = decideCertificate ?? throw new ArgumentNullException(nameof(decideCertificate));
        _connect = connect ?? NativeRdpSession.Connect;
        _callbacks = new NativeSessionCallbacks(
            OnFrame, OnClipboardText, OnClipboardFiles, OnStatus, OnCertificate, OnCursor, OnClipboardFileContents);
    }

    internal event Action<int, int>? FrameReceived;
    internal event Action<string>? ClipboardTextReceived;
    internal event Action<RemoteFileOffer>? ClipboardFilesReceived;
    internal event Action<int, RdpSessionError?>? StatusReceived;
    internal event Action<RemoteCursorDescriptor>? CursorReceived;

    internal bool HasHandle => !IsDisposeStarted && Volatile.Read(ref _handle) != IntPtr.Zero;
    internal bool IsDisposeStarted => Volatile.Read(ref _disposeStarted) != 0;

    internal IntPtr Start(RdpSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lifetimeLock)
        {
            if (_started || IsDisposeStarted) throw new InvalidOperationException("This runtime cannot be started again.");
            _started = true;
            Volatile.Write(ref _initializing, 1);
        }
        try
        {
            var session = _connect(request, _callbacks);
            lock (_lifetimeLock)
            {
                if (!IsDisposeStarted)
                {
                    _session = session;
                    Volatile.Write(ref _handle, session.Handle);
                    return session.Handle;
                }
            }

            // Dispose can win while the connector is running. Never publish a handle afterwards.
            if (session.Handle != IntPtr.Zero) session.Free();
            GC.KeepAlive(_callbacks);
            return IntPtr.Zero;
        }
        finally
        {
            Volatile.Write(ref _initializing, 0);
        }
    }

    internal bool TryGetActiveSession([NotNullWhen(true)] out INativeRdpSession? session)
    {
        session = _session;
        return HasHandle && session != null;
    }

    private bool IsActive(IntPtr handle) => HasHandle && handle == Volatile.Read(ref _handle);

    private bool IsActiveOrInitializing(IntPtr handle) => IsActive(handle) ||
        (!IsDisposeStarted && handle != IntPtr.Zero && Volatile.Read(ref _initializing) != 0);

    private void OnFrame(IntPtr handle, IntPtr data, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight, int sourceStride)
    {
        if (IsActive(handle)) FrameReceived?.Invoke(width, height);
    }

    private void OnClipboardText(IntPtr handle, IntPtr text)
    {
        if (!IsActive(handle)) return;
        var value = Marshal.PtrToStringUTF8(text) ?? "";
        if (!IsDisposeStarted) ClipboardTextReceived?.Invoke(value);
    }

    private void OnClipboardFiles(IntPtr handle, uint generation, IntPtr descriptor, nint descriptorSize)
    {
        if (!IsActive(handle) || descriptor == IntPtr.Zero || descriptorSize <= 0) return;
        var bytes = new byte[checked((int)descriptorSize)];
        Marshal.Copy(descriptor, bytes, 0, bytes.Length);
        var files = RemoteFileList.TryParse(generation, bytes);
        if (files != null && !IsDisposeStarted) ClipboardFilesReceived?.Invoke(new RemoteFileOffer(files, this));
    }

    public Task<byte[]?> RequestFileContentsAsync(uint generation, uint listIndex, bool sizeOnly, long offset, int size, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !TryGetActiveSession(out var session)) return Task.FromResult<byte[]?>(null);
        var streamId = (uint)Interlocked.Increment(ref _nextFileContentsStreamId);
        var request = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileContentsRequests[streamId] = request;
        if (IsDisposeStarted || !session.RequestRemoteFileContents(generation, streamId, listIndex, sizeOnly, (ulong)offset, (uint)size))
        {
            CompleteFileContents(streamId, null);
            return request.Task;
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => CompleteFileContents(streamId, null));
            request.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return request.Task;
    }

    private void OnClipboardFileContents(IntPtr handle, uint streamId, bool ok, IntPtr data, uint size)
    {
        if (!_fileContentsRequests.ContainsKey(streamId)) return;
        byte[]? bytes = null;
        if (ok && IsActive(handle) && (data != IntPtr.Zero || size == 0))
        {
            bytes = new byte[checked((int)size)];
            if (size > 0) Marshal.Copy(data, bytes, 0, bytes.Length);
        }
        CompleteFileContents(streamId, bytes);
    }

    private void CompleteFileContents(uint streamId, byte[]? data)
    {
        if (_fileContentsRequests.TryRemove(streamId, out var request)) request.TrySetResult(data);
    }

    private void OnStatus(IntPtr handle, int status, uint errorCode, IntPtr errorName, IntPtr errorMessage)
    {
        if (!IsActiveOrInitializing(handle)) return;
        var error = status == 2
            ? RdpSessionError.Create(errorCode, Marshal.PtrToStringUTF8(errorName), Marshal.PtrToStringUTF8(errorMessage))
            : null;
        // The view model posts to the UI thread and rechecks lifetime before applying the payload.
        StatusReceived?.Invoke(status, error);
    }

    private void OnCursor(IntPtr handle, int kind, uint cursorId, int width, int height, int hotX, int hotY)
    {
        if (IsActive(handle)) CursorReceived?.Invoke(new RemoteCursorDescriptor((RemoteCursorKind)kind, cursorId, width, height, hotX, hotY));
    }

    private int OnCertificate(IntPtr handle, IntPtr host, ushort port, IntPtr commonName, IntPtr subject,
        IntPtr issuer, IntPtr fingerprint, int isChanged, IntPtr previousSubject, IntPtr previousIssuer, IntPtr previousFingerprint)
    {
        if (!IsActiveOrInitializing(handle)) return (int)CertificateTrustDecision.Reject;
        var prompt = new RdpCertificatePrompt(
            Marshal.PtrToStringUTF8(host) ?? _host,
            port,
            Marshal.PtrToStringUTF8(commonName) ?? "",
            Marshal.PtrToStringUTF8(subject) ?? "",
            Marshal.PtrToStringUTF8(issuer) ?? "",
            Marshal.PtrToStringUTF8(fingerprint) ?? "",
            isChanged != 0,
            Marshal.PtrToStringUTF8(previousSubject),
            Marshal.PtrToStringUTF8(previousIssuer),
            Marshal.PtrToStringUTF8(previousFingerprint));
        return IsDisposeStarted ? (int)CertificateTrustDecision.Reject : (int)_decideCertificate(prompt);
    }

    public void Dispose()
    {
        IntPtr handle;
        INativeRdpSession? session;
        lock (_lifetimeLock)
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            session = Interlocked.Exchange(ref _session, null);
        }
        if (handle != IntPtr.Zero) session?.Free();
        foreach (var streamId in _fileContentsRequests.Keys) CompleteFileContents(streamId, null);
        GC.KeepAlive(_callbacks);
    }
}

internal readonly record struct RemoteCursorDescriptor(RemoteCursorKind Kind, uint CursorId, int Width, int Height, int HotX, int HotY);

/// <summary>The native ABI's callback set, kept strongly reachable by the owning runtime.</summary>
internal sealed record NativeSessionCallbacks(
    NativeWrapper.FrameCallback Frame,
    NativeWrapper.ClipboardTextCallback ClipboardText,
    NativeWrapper.ClipboardFilesCallback ClipboardFiles,
    NativeWrapper.StatusCallback Status,
    NativeWrapper.CertificateDecisionCallback Certificate,
    NativeWrapper.CursorCallback Cursor,
    NativeWrapper.ClipboardFileContentsCallback ClipboardFileContents);

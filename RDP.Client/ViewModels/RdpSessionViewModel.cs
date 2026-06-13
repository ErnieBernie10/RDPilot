using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RDP.Client.Models;

namespace RDP.Client.ViewModels;

public partial class RdpSessionViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private WriteableBitmap? _screen;
    [ObservableProperty] private RdpSessionStatus _status = RdpSessionStatus.Connecting;
    [ObservableProperty] private RdpSessionError? _lastError;

    private readonly NativeWrapper.FrameCallback _frameCallback;
    private readonly NativeWrapper.ClipboardTextCallback _clipboardCallback;
    private readonly NativeWrapper.StatusCallback _statusCallback;
    private readonly NativeWrapper.CertificateDecisionCallback _certificateDecisionCallback;
    private readonly Action<RdpSessionViewModel, string> _remoteClipboardTextReceived;
    private readonly Func<RdpCertificatePrompt, CertificateTrustDecision> _certificateTrustDecision;
    private readonly ManagedFramePresenter _framePresenter;
    private IntPtr _handle;
    private int _disposeStarted;
    private int _disposed;
    private int _requestedWidth;
    private int _requestedHeight;

    public RdpSessionViewModel(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived,
        Func<RdpCertificatePrompt, CertificateTrustDecision> certificateTrustDecision)
    {
        Connection = connection.Clone();
        Title = connection.Name;
        _requestedWidth = width;
        _requestedHeight = height;
        _remoteClipboardTextReceived = remoteClipboardTextReceived;
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _statusCallback = OnStatusChanged;
        _certificateDecisionCallback = OnCertificateDecisionRequested;
        _certificateTrustDecision = certificateTrustDecision;
        _framePresenter = new ManagedFramePresenter(Title, width, height, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty));

        _handle = NativeWrapper.rdp_session_connect(
            connection.Host,
            connection.Domain,
            connection.Username,
            password,
            connection.GatewayHost,
            connection.GatewayDomain,
            connection.GatewayUsername,
            gatewayPassword,
            width,
            height,
            _frameCallback,
            _clipboardCallback,
            _statusCallback,
            _certificateDecisionCallback);

        if (_handle == IntPtr.Zero)
        {
            LastError = new RdpSessionError(0, "WRAPPER_SESSION_CREATE_FAILED", "Failed to start the RDP session.", RdpSessionErrorKind.Unknown);
            Status = RdpSessionStatus.Failed;
        }
        else
        {
            Status = RdpSessionStatus.Connecting;
        }
    }

    internal RdpSessionViewModel(
        SavedConnection connection,
        RdpSessionStatus status,
        RdpSessionError? error = null)
    {
        Connection = connection.Clone();
        Title = connection.Name;
        _remoteClipboardTextReceived = static (_, _) => { };
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _statusCallback = OnStatusChanged;
        _certificateDecisionCallback = OnCertificateDecisionRequested;
        _certificateTrustDecision = static _ => CertificateTrustDecision.Reject;
        _framePresenter = new ManagedFramePresenter(Title, 1, 1, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), initializeBitmap: false);
        LastError = error;
        Status = status;
    }

    public SavedConnection Connection { get; }
    public string Title { get; }
    public string StatusText => Status switch
    {
        RdpSessionStatus.Connecting => "Connecting",
        RdpSessionStatus.Connected => "Connected",
        RdpSessionStatus.Disconnecting => "Disconnecting",
        RdpSessionStatus.Disconnected => "Disconnected",
        RdpSessionStatus.Failed => "Failed",
        _ => Status.ToString()
    };
    public string? ErrorText => LastError?.Message;
    public bool IsConnected => _handle != IntPtr.Zero && Status == RdpSessionStatus.Connected;
    public bool CanDisconnect => Status is RdpSessionStatus.Connecting or RdpSessionStatus.Connected;
    public bool CanReconnect => Status is RdpSessionStatus.Failed or RdpSessionStatus.Disconnected;
    public event EventHandler? RequestRedraw;

    internal void SetTestStatus(RdpSessionStatus status, RdpSessionError? error = null)
    {
        LastError = error;
        Status = status;
    }

    partial void OnStatusChanged(RdpSessionStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanReconnect));
    }

    partial void OnLastErrorChanged(RdpSessionError? value)
    {
        OnPropertyChanged(nameof(ErrorText));
    }

    public async Task DisconnectAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var handle = _handle;
        if (handle == IntPtr.Zero)
        {
            if (!IsDisposeStarted)
            {
                LastError = null;
                Status = RdpSessionStatus.Disconnected;
            }
            return;
        }

        Status = RdpSessionStatus.Disconnecting;
        await Task.Run(() => NativeWrapper.rdp_session_disconnect(handle));
        if (IsDisposeStarted)
        {
            return;
        }

        LastError = null;
        Status = RdpSessionStatus.Disconnected;
    }

    public void UpdateResolution(int width, int height)
    {
        if (!TryGetActiveHandle(out var handle) || width <= 0 || height <= 0)
        {
            return;
        }

        _requestedWidth = width;
        _requestedHeight = height;
        NativeWrapper.rdp_session_update_resolution(handle, width, height);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        if (!TryGetActiveHandle(out var handle)) return;
        _framePresenter.MarkInputSent();
        NativeWrapper.rdp_session_send_mouse_event(handle, flags, x, y);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        if (!TryGetActiveHandle(out var handle)) return;
        _framePresenter.MarkInputSent();
        NativeWrapper.rdp_session_send_keyboard_event(handle, flags, code);
    }

    public void SetLocalClipboardText(string text)
    {
        if (!TryGetActiveHandle(out var handle)) return;
        NativeWrapper.rdp_session_clipboard_set_local_text(handle, text);
    }

    private void OnRemoteClipboardTextReceived(IntPtr session, IntPtr textPtr)
    {
        if (!IsActiveCallbackSession(session)) return;
        var text = Marshal.PtrToStringUTF8(textPtr) ?? "";
        if (IsDisposeStarted) return;
        _remoteClipboardTextReceived(this, text);
    }

    private void OnStatusChanged(IntPtr session, int status, uint errorCode, IntPtr errorNamePtr, IntPtr errorMessagePtr)
    {
        if (!IsActiveCallbackSession(session)) return;

        var statusValue = status switch
        {
            1 => RdpSessionStatus.Connected,
            2 => RdpSessionStatus.Failed,
            3 => RdpSessionStatus.Disconnected,
            _ => Status
        };
        var errorName = Marshal.PtrToStringUTF8(errorNamePtr);
        var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr);
        var error = statusValue == RdpSessionStatus.Failed
            ? RdpSessionError.Create(errorCode, errorName, errorMessage)
            : null;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsActiveCallbackSession(session)) return;
            LastError = error;
            Status = statusValue;
        });
    }

    private void OnFrameReceived(IntPtr session, IntPtr data, int width, int height)
    {
        if (!IsActiveCallbackSession(session)) return;
        _framePresenter.EnqueueFrame(data, width, height);
    }

    private int OnCertificateDecisionRequested(
        IntPtr session,
        IntPtr hostPtr,
        ushort port,
        IntPtr commonNamePtr,
        IntPtr subjectPtr,
        IntPtr issuerPtr,
        IntPtr fingerprintPtr,
        int isChanged,
        IntPtr previousSubjectPtr,
        IntPtr previousIssuerPtr,
        IntPtr previousFingerprintPtr)
    {
        if (!IsActiveCallbackSession(session)) return (int)CertificateTrustDecision.Reject;

        var prompt = new RdpCertificatePrompt(
            Marshal.PtrToStringUTF8(hostPtr) ?? Connection.Host,
            port,
            Marshal.PtrToStringUTF8(commonNamePtr) ?? "",
            Marshal.PtrToStringUTF8(subjectPtr) ?? "",
            Marshal.PtrToStringUTF8(issuerPtr) ?? "",
            Marshal.PtrToStringUTF8(fingerprintPtr) ?? "",
            isChanged != 0,
            Marshal.PtrToStringUTF8(previousSubjectPtr),
            Marshal.PtrToStringUTF8(previousIssuerPtr),
            Marshal.PtrToStringUTF8(previousFingerprintPtr));

        return (int)_certificateTrustDecision(prompt);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeWrapper.rdp_session_free(handle);
        }

        _framePresenter.Dispose();
        Interlocked.Exchange(ref _disposed, 1);
    }

    private bool TryGetActiveHandle(out IntPtr handle)
    {
        handle = _handle;
        return !IsDisposeStarted && handle != IntPtr.Zero;
    }

    private bool IsActiveCallbackSession(IntPtr session)
    {
        var handle = _handle;
        return !IsDisposeStarted && handle != IntPtr.Zero && session == handle;
    }

    private bool IsDisposeStarted => Volatile.Read(ref _disposeStarted) != 0;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

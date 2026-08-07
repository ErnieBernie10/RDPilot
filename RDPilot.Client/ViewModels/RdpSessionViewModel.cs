using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RDPilot.Client.Models;

namespace RDPilot.Client.ViewModels;

public partial class RdpSessionViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private WriteableBitmap? _screen;

    public double DisplayWidth
    {
        get
        {
            if (Screen == null || _renderScaling <= 0) return 0;
            return Screen.PixelSize.Width / _renderScaling;
        }
    }

    public double DisplayHeight
    {
        get
        {
            if (Screen == null || _renderScaling <= 0) return 0;
            return Screen.PixelSize.Height / _renderScaling;
        }
    }

    partial void OnScreenChanged(WriteableBitmap? value)
    {
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }
    [ObservableProperty] private RdpSessionStatus _status = RdpSessionStatus.Connecting;
    [ObservableProperty] private RdpSessionError? _lastError;

    /// <summary>
    /// Transient per-session keyboard grab state. Deliberately not persisted: every connect
    /// starts ungrabbed.
    /// </summary>
    [ObservableProperty] private bool _isKeyboardGrabbed;

    private readonly NativeWrapper.FrameCallback _frameCallback;
    private readonly NativeWrapper.ClipboardTextCallback _clipboardCallback;
    private readonly NativeWrapper.ClipboardFilesCallback _clipboardFilesCallback;
    private readonly NativeWrapper.StatusCallback _statusCallback;
    private readonly NativeWrapper.CertificateDecisionCallback _certificateDecisionCallback;
    private readonly Action<RdpSessionViewModel, string> _remoteClipboardTextReceived;
    private readonly Action<RdpSessionViewModel, string[]> _remoteClipboardFilesReceived;
    private readonly Func<RdpCertificatePrompt, CertificateTrustDecision> _certificateTrustDecision;
    private readonly ManagedFramePresenter _framePresenter;
    private INativeRdpSession? _nativeSession;
    private IntPtr _handle;
    private int _initializingNativeSession;
    private int _disposeStarted;
    private int _disposed;
    private int _requestedWidth;
    private int _requestedHeight;
    private double _renderScaling = 1.0;
    private uint _dpiScalePercent = 100;

    public RdpSessionViewModel(
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
        Action<RdpSessionViewModel, string[]> remoteClipboardFilesReceived,
        Func<RdpCertificatePrompt, CertificateTrustDecision> certificateTrustDecision)
    {
        Connection = connection.Clone();
        Title = connection.Name;
        _renderScaling = renderScaling > 0 ? renderScaling : 1.0;
        _dpiScalePercent = RdpSessionOptions.ClampDpiScalePercent((uint)Math.Max(100, Math.Round(_renderScaling * 100)));
        (width, height) = RdpSessionOptions.NormalizeResolution(width, height);
        colorDepth = RdpSessionOptions.NormalizeColorDepth(colorDepth);
        _requestedWidth = width;
        _requestedHeight = height;
        _remoteClipboardTextReceived = remoteClipboardTextReceived;
        _remoteClipboardFilesReceived = remoteClipboardFilesReceived;
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _clipboardFilesCallback = OnRemoteClipboardFilesReceived;
        _statusCallback = OnStatusChanged;
        _certificateDecisionCallback = OnCertificateDecisionRequested;
        _certificateTrustDecision = certificateTrustDecision;
        _framePresenter = new ManagedFramePresenter(Title, width, height, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), PresentPending, _renderScaling);

        try
        {
            var connectHost = NativeWrapper.ResolveDirectConnectHost(connection.Host);
            var keyboardLayout = NativeWrapper.GetCurrentKeyboardLayout();
            var networkSettings = RdpSessionOptions.NormalizeNetworkSettings(connectionType);
            Volatile.Write(ref _initializingNativeSession, 1);
            _nativeSession = NativeRdpSession.Connect(
                connection.Host,
                connectHost,
                RdpSessionOptions.NormalizePort(connection.Port),
                connection.Domain,
                connection.Username,
                password,
                connection.GatewayHost,
                connection.GatewayDomain,
                connection.GatewayUsername,
                gatewayPassword,
                width,
                height,
                colorDepth,
                compression,
                fontSmoothing,
                bitmapCache,
                desktopWallpaper,
                themes,
                menuAnimations,
                fullWindowDrag,
                networkSettings.ConnectionType,
                networkSettings.NetworkAutoDetect,
                keyboardLayout,
                _dpiScalePercent,
                _frameCallback,
                _clipboardCallback,
                _clipboardFilesCallback,
                _statusCallback,
                _certificateDecisionCallback);
            _handle = _nativeSession.Handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = new RdpSessionError(0, "WRAPPER_NATIVE_LOAD_FAILED", ex.Message, RdpSessionErrorKind.Unknown);
            Status = RdpSessionStatus.Failed;
            return;
        }
        finally
        {
            Volatile.Write(ref _initializingNativeSession, 0);
        }

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
        _remoteClipboardFilesReceived = static (_, _) => { };
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _clipboardFilesCallback = OnRemoteClipboardFilesReceived;
        _statusCallback = OnStatusChanged;
        _certificateDecisionCallback = OnCertificateDecisionRequested;
        _certificateTrustDecision = static _ => CertificateTrustDecision.Reject;
        _framePresenter = new ManagedFramePresenter(Title, 1, 1, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), PresentPending, initializeBitmap: false);
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
    public bool IsConnecting => Status is RdpSessionStatus.Connecting or RdpSessionStatus.Disconnecting;
    public bool IsFailed => Status == RdpSessionStatus.Failed;
    public bool IsDisconnected => Status == RdpSessionStatus.Disconnected;
    public bool CanDisconnect => Status is RdpSessionStatus.Connecting or RdpSessionStatus.Connected;
    public bool CanReconnect => Status is RdpSessionStatus.Failed or RdpSessionStatus.Disconnected;
    public event EventHandler? RequestRedraw;

    internal void SuspendPresentation()
    {
        _framePresenter.Suspend();
    }

    internal void ResumePresentation()
    {
        if (IsDisposed)
        {
            return;
        }

        if (TryGetActiveSession(out var nativeSession))
        {
            nativeSession.RequestFullFrame();
        }
        _framePresenter.Resume();
    }

    internal void SetTestStatus(RdpSessionStatus status, RdpSessionError? error = null)
    {
        LastError = error;
        Status = status;
    }

    partial void OnStatusChanged(RdpSessionStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanReconnect));

        if (value != RdpSessionStatus.Connected)
        {
            IsKeyboardGrabbed = false;
        }
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

        if (!TryGetActiveSession(out var nativeSession))
        {
            if (!IsDisposeStarted)
            {
                LastError = null;
                Status = RdpSessionStatus.Disconnected;
            }
            return;
        }

        Status = RdpSessionStatus.Disconnecting;
        await Task.Run(nativeSession.Disconnect);
        if (IsDisposeStarted)
        {
            return;
        }

        LastError = null;
        Status = RdpSessionStatus.Disconnected;
    }

    public void UpdateResolution(int width, int height, double renderScaling = 0)
    {
        if (!TryGetActiveSession(out var nativeSession) || width <= 0 || height <= 0)
        {
            return;
        }

        if (renderScaling > 0)
        {
            _renderScaling = renderScaling;
            _framePresenter.UpdateRenderScaling(renderScaling);
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }

        (width, height) = RdpSessionOptions.NormalizeResolution(width, height);
        _requestedWidth = width;
        _requestedHeight = height;
        nativeSession.UpdateResolution(width, height, _dpiScalePercent);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        if (!TryGetActiveSession(out var nativeSession)) return;
        _framePresenter.MarkInputSent();
        nativeSession.SendMouseEvent(flags, x, y);
    }

    public void SendMouseEventScaled(ushort flags, double dipX, double dipY)
    {
        if (!TryGetActiveSession(out var nativeSession)) return;
        _framePresenter.MarkInputSent();
        ushort px = (ushort)Math.Clamp(dipX * _renderScaling, 0, 65535);
        ushort py = (ushort)Math.Clamp(dipY * _renderScaling, 0, 65535);
        nativeSession.SendMouseEvent(flags, px, py);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        if (!TryGetActiveSession(out var nativeSession)) return;
        _framePresenter.MarkInputSent();
        nativeSession.SendKeyboardEvent(flags, code);
    }

    /// <summary>
    /// Sends Ctrl+Alt+Del to the remote host. The secure attention sequence can never be
    /// intercepted locally, so this explicit action is the only way to deliver it.
    /// </summary>
    public void SendCtrlAltDel()
    {
        const ushort leftCtrl = 0x1D;
        const ushort leftAlt = 0x38;
        const ushort delete = 0x53;
        const ushort release = 0x8000;
        const ushort extended = 0x0100;

        SendKeyboardEvent(0, leftCtrl);
        SendKeyboardEvent(0, leftAlt);
        SendKeyboardEvent(extended, delete);
        SendKeyboardEvent(release | extended, delete);
        SendKeyboardEvent(release, leftAlt);
        SendKeyboardEvent(release, leftCtrl);
    }

    public void SetLocalClipboardText(string text)
    {
        if (!TryGetActiveSession(out var nativeSession)) return;
        nativeSession.SetLocalClipboardText(text);
    }

    public void SetLocalClipboardFiles(string[] filePaths)
    {
        if (!TryGetActiveSession(out var nativeSession) || filePaths == null) return;

        nativeSession.SetLocalClipboardFiles(filePaths);
    }

    public void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height)
    {
        if (!TryGetActiveSession(out var nativeSession) || bitmapData == null || bitmapData.Length == 0) return;
        
        var bitmapHandle = GCHandle.Alloc(bitmapData, GCHandleType.Pinned);
        try
        {
            nativeSession.SetLocalClipboardBitmap(bitmapHandle.AddrOfPinnedObject(), bitmapData.Length, width, height);
        }
        finally
        {
            bitmapHandle.Free();
        }
    }

    private void OnRemoteClipboardTextReceived(IntPtr session, IntPtr textPtr)
    {
        if (!IsActiveCallbackSession(session)) return;
        var text = Marshal.PtrToStringUTF8(textPtr) ?? "";
        if (IsDisposeStarted) return;
        _remoteClipboardTextReceived(this, text);
    }

    private void OnRemoteClipboardFilesReceived(IntPtr session, IntPtr filePathsPtr, nint fileCount)
    {
        if (!IsActiveCallbackSession(session) || IsDisposeStarted || filePathsPtr == IntPtr.Zero || fileCount <= 0)
        {
            return;
        }

        var count = checked((int)fileCount);
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            var pathPtr = Marshal.ReadIntPtr(filePathsPtr, i * IntPtr.Size);
            paths[i] = Marshal.PtrToStringUTF8(pathPtr) ?? string.Empty;
        }

        _remoteClipboardFilesReceived(this, paths);
    }

    private void OnStatusChanged(IntPtr session, int status, uint errorCode, IntPtr errorNamePtr, IntPtr errorMessagePtr)
    {
        if (!IsCurrentOrInitializingCallbackSession(session)) return;

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
            if (!IsCurrentOrInitializingCallbackSession(session)) return;
            LastError = error;
            Status = statusValue;
        });
    }

    private void OnFrameReceived(IntPtr session, IntPtr data, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight, int sourceStride)
    {
        if (!IsActiveCallbackSession(session)) return;
        _framePresenter.EnqueueFrame(width, height);
    }

    private bool PresentPending(IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight)
    {
        if (!TryGetActiveSession(out var nativeSession))
        {
            dirtyX = dirtyY = dirtyWidth = dirtyHeight = 0;
            fbWidth = fbHeight = 0;
            return false;
        }

        return nativeSession.Present(dest, destStride, destWidth, destHeight, out dirtyX, out dirtyY, out dirtyWidth, out dirtyHeight, out fbWidth, out fbHeight);
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
        if (!IsCurrentOrInitializingCallbackSession(session))
        {
            return (int)CertificateTrustDecision.Reject;
        }

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

        _framePresenter.Dispose();

        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        var nativeSession = Interlocked.Exchange(ref _nativeSession, null);
        if (handle != IntPtr.Zero && nativeSession != null)
        {
            nativeSession.Free();
        }
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }

    private bool TryGetActiveSession([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INativeRdpSession? nativeSession)
    {
        nativeSession = _nativeSession;
        return !IsDisposeStarted && nativeSession != null && _handle != IntPtr.Zero;
    }

    private bool IsActiveCallbackSession(IntPtr session)
    {
        var handle = _handle;
        return !IsDisposeStarted && handle != IntPtr.Zero && session == handle;
    }

    private bool IsCurrentOrInitializingCallbackSession(IntPtr session)
    {
        return IsActiveCallbackSession(session) ||
            (!IsDisposeStarted && session != IntPtr.Zero && Volatile.Read(ref _initializingNativeSession) != 0);
    }

    private bool IsDisposeStarted => Volatile.Read(ref _disposeStarted) != 0;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

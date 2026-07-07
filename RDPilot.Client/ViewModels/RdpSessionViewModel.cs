using System;
using System.Runtime.InteropServices;
using System.Text;
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
        Func<RdpCertificatePrompt, CertificateTrustDecision> certificateTrustDecision)
    {
        Connection = connection.Clone();
        Title = connection.Name;
        _requestedWidth = width;
        _requestedHeight = height;
        _renderScaling = renderScaling > 0 ? renderScaling : 1.0;
        _dpiScalePercent = (uint)Math.Max(100, Math.Round(_renderScaling * 100));
        _remoteClipboardTextReceived = remoteClipboardTextReceived;
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _statusCallback = OnStatusChanged;
        _certificateDecisionCallback = OnCertificateDecisionRequested;
        _certificateTrustDecision = certificateTrustDecision;
        _framePresenter = new ManagedFramePresenter(Title, width, height, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), PresentPending, _renderScaling);

        try
        {
            var connectHost = NativeWrapper.ResolveDirectConnectHost(connection.Host);
            var keyboardLayout = NativeWrapper.GetCurrentKeyboardLayout();
            _handle = NativeWrapper.rdp_session_connect(
                connection.Host,
                connectHost,
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
                (int)connectionType,
                keyboardLayout,
                _dpiScalePercent,
                _frameCallback,
                _clipboardCallback,
                _statusCallback,
                _certificateDecisionCallback);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = new RdpSessionError(0, "WRAPPER_NATIVE_LOAD_FAILED", ex.Message, RdpSessionErrorKind.Unknown);
            Status = RdpSessionStatus.Failed;
            return;
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
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
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

    public void UpdateResolution(int width, int height, double renderScaling = 0)
    {
        if (!TryGetActiveHandle(out var handle) || width <= 0 || height <= 0)
        {
            return;
        }

        if (renderScaling > 0)
        {
            _renderScaling = renderScaling;
            _framePresenter.UpdateRenderScaling(renderScaling);
        }

        _requestedWidth = width;
        _requestedHeight = height;
        NativeWrapper.rdp_session_update_resolution(handle, width, height, _dpiScalePercent);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        if (!TryGetActiveHandle(out var handle)) return;
        _framePresenter.MarkInputSent();
        NativeWrapper.rdp_session_send_mouse_event(handle, flags, x, y);
    }

    public void SendMouseEventScaled(ushort flags, double dipX, double dipY)
    {
        if (!TryGetActiveHandle(out var handle)) return;
        _framePresenter.MarkInputSent();
        ushort px = (ushort)Math.Clamp(dipX * _renderScaling, 0, 65535);
        ushort py = (ushort)Math.Clamp(dipY * _renderScaling, 0, 65535);
        NativeWrapper.rdp_session_send_mouse_event(handle, flags, px, py);
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

    public void SetLocalClipboardFiles(string[] filePaths)
    {
        if (!TryGetActiveHandle(out var handle) || filePaths == null || filePaths.Length == 0) return;
        
        var ptrs = new IntPtr[filePaths.Length];
        var pathHandles = new GCHandle[filePaths.Length];
        try
        {
            for (int i = 0; i < filePaths.Length; i++)
            {
                var pathBytes = Encoding.UTF8.GetBytes(filePaths[i] + "\0");
                pathHandles[i] = GCHandle.Alloc(pathBytes, GCHandleType.Pinned);
                ptrs[i] = pathHandles[i].AddrOfPinnedObject();
            }
            
            var ptrArrayHandle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try
            {
                NativeWrapper.rdp_session_clipboard_set_local_files(handle, ptrArrayHandle.AddrOfPinnedObject(), filePaths.Length);
            }
            finally
            {
                ptrArrayHandle.Free();
            }
        }
        finally
        {
            foreach (var fileHandle in pathHandles)
            {
                if (fileHandle.IsAllocated)
                    fileHandle.Free();
            }
        }
    }

    public void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height)
    {
        if (!TryGetActiveHandle(out var handle) || bitmapData == null || bitmapData.Length == 0) return;
        
        var bitmapHandle = GCHandle.Alloc(bitmapData, GCHandleType.Pinned);
        try
        {
            NativeWrapper.rdp_session_clipboard_set_local_bitmap(handle, bitmapHandle.AddrOfPinnedObject(), bitmapData.Length, width, height);
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

    private void OnFrameReceived(IntPtr session, IntPtr data, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight, int sourceStride)
    {
        if (!IsActiveCallbackSession(session)) return;
        _framePresenter.EnqueueFrame(width, height);
    }

    private bool PresentPending(IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight)
    {
        if (!TryGetActiveHandle(out var handle))
        {
            dirtyX = dirtyY = dirtyWidth = dirtyHeight = 0;
            fbWidth = fbHeight = 0;
            return false;
        }
        return NativeWrapper.rdp_session_present(handle, dest, destStride, destWidth, destHeight, out dirtyX, out dirtyY, out dirtyWidth, out dirtyHeight, out fbWidth, out fbHeight);
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
        GC.SuppressFinalize(this);
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

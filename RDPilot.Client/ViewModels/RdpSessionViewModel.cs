using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
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

    /// <summary>
    /// The cursor the remote session wants shown over the viewport. FreeRDP never draws the pointer
    /// into the desktop framebuffer, so this is the only thing that makes the remote cursor visible.
    /// Null means "no opinion" and the viewport falls back to its inherited cursor.
    /// </summary>
    [ObservableProperty] private Cursor? _remoteCursor;

    private readonly RdpSessionRuntime _runtime;
    private readonly ManagedFramePresenter _framePresenter;
    private readonly RemoteCursorCache _cursorCache;
    private readonly object _cursorLock = new();
    private RemoteCursorDescriptor? _pendingCursor;
    private int _cursorApplyQueued;
    private int _disposeStarted;
    private int _disposed;
    private double _renderScaling = 1.0;
    private uint _dpiScalePercent = 100;

    public RdpSessionViewModel(
        RdpSessionStartRequest request,
        Func<RdpCertificatePrompt, CertificateTrustDecision> certificateTrustDecision)
        : this(request, new RdpSessionRuntime((request ?? throw new ArgumentNullException(nameof(request))).Connection.Host, certificateTrustDecision))
    {
    }

    internal RdpSessionViewModel(RdpSessionStartRequest request, RdpSessionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Connection = request.Connection;
        Title = Connection.Name;
        _renderScaling = request.RenderScaling;
        _dpiScalePercent = request.DpiScalePercent;
        _framePresenter = new ManagedFramePresenter(Title, request.Width, request.Height, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), PresentPending, _renderScaling);
        _cursorCache = new RemoteCursorCache(CopyCursorImage);
        SubscribeRuntime();
    }

    /// <summary>Starts native delivery only after the session and its presentation consumers are assembled.</summary>
    /// <param name="request">Transient connection inputs; these are not retained by the view model.</param>
    public void Start(RdpSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            _runtime.Start(request);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = new RdpSessionError(0, "WRAPPER_NATIVE_LOAD_FAILED", ex.Message, RdpSessionErrorKind.Unknown);
            Status = RdpSessionStatus.Failed;
            return;
        }
        if (!_runtime.HasHandle)
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
        _runtime = new RdpSessionRuntime(connection.Host, static _ => CertificateTrustDecision.Reject);
        Connection = connection.Clone();
        Title = connection.Name;
        _framePresenter = new ManagedFramePresenter(Title, 1, 1, screen => Screen = screen, () => RequestRedraw?.Invoke(this, EventArgs.Empty), PresentPending, initializeBitmap: false);
        _cursorCache = new RemoteCursorCache(CopyCursorImage);
        SubscribeRuntime();
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
    public bool IsConnected => _runtime.HasHandle && Status == RdpSessionStatus.Connected;
    public bool IsConnecting => Status is RdpSessionStatus.Connecting or RdpSessionStatus.Disconnecting;
    public bool IsFailed => Status == RdpSessionStatus.Failed;
    public bool IsDisconnected => Status == RdpSessionStatus.Disconnected;
    public bool CanDisconnect => Status is RdpSessionStatus.Connecting or RdpSessionStatus.Connected;
    public bool CanReconnect => Status is RdpSessionStatus.Failed or RdpSessionStatus.Disconnected;
    public event EventHandler? RequestRedraw;
    public event EventHandler<string>? RemoteClipboardTextReceived;
    public event EventHandler<RemoteFileOffer>? RemoteClipboardFilesReceived;

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

        if (_runtime.TryGetActiveSession(out var nativeSession))
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

        if (!_runtime.TryGetActiveSession(out var nativeSession))
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
        if (!_runtime.TryGetActiveSession(out var nativeSession) || width <= 0 || height <= 0)
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
        nativeSession.UpdateResolution(width, height, _dpiScalePercent);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession)) return;
        _framePresenter.MarkInputSent();
        nativeSession.SendMouseEvent(flags, x, y);
    }

    public void SendMouseEventScaled(ushort flags, double dipX, double dipY)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession)) return;
        _framePresenter.MarkInputSent();
        ushort px = (ushort)Math.Clamp(dipX * _renderScaling, 0, 65535);
        ushort py = (ushort)Math.Clamp(dipY * _renderScaling, 0, 65535);
        nativeSession.SendMouseEvent(flags, px, py);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession)) return;
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
        if (!_runtime.TryGetActiveSession(out var nativeSession)) return;
        nativeSession.SetLocalClipboardText(text);
    }

    public void SetLocalClipboardFiles(string[] filePaths)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession) || filePaths == null) return;

        nativeSession.SetLocalClipboardFiles(filePaths);
    }

    public void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession) || bitmapData == null || bitmapData.Length == 0) return;
        
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

    private void SubscribeRuntime()
    {
        _runtime.FrameReceived += OnFrameReceived;
        _runtime.ClipboardTextReceived += OnRemoteClipboardTextReceived;
        _runtime.ClipboardFilesReceived += OnRemoteClipboardFilesReceived;
        _runtime.StatusReceived += OnNativeStatusChanged;
        _runtime.CursorReceived += OnCursorChanged;
    }

    private void OnRemoteClipboardTextReceived(string text)
    {
        if (!IsDisposeStarted) RemoteClipboardTextReceived?.Invoke(this, text);
    }

    private void OnRemoteClipboardFilesReceived(RemoteFileOffer offer)
    {
        if (!IsDisposeStarted) RemoteClipboardFilesReceived?.Invoke(this, offer);
    }

    private void OnNativeStatusChanged(int status, RdpSessionError? error)
    {
        var statusValue = status switch
        {
            1 => RdpSessionStatus.Connected,
            2 => RdpSessionStatus.Failed,
            3 => RdpSessionStatus.Disconnected,
            _ => Status
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_runtime.HasHandle || IsDisposeStarted) return;
            LastError = error;
            Status = statusValue;
        });
    }

    private void OnFrameReceived(int width, int height)
    {
        if (!IsDisposeStarted) _framePresenter.EnqueueFrame(width, height);
    }

    private bool PresentPending(IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight)
    {
        if (!_runtime.TryGetActiveSession(out var nativeSession))
        {
            dirtyX = dirtyY = dirtyWidth = dirtyHeight = 0;
            fbWidth = fbHeight = 0;
            return false;
        }

        return nativeSession.Present(dest, destStride, destWidth, destHeight, out dirtyX, out dirtyY, out dirtyWidth, out dirtyHeight, out fbWidth, out fbHeight);
    }

    /// <summary>
    /// Runs on the RDP thread. Records the newest descriptor and posts at most one UI-thread apply,
    /// mirroring how <see cref="ManagedFramePresenter.EnqueueFrame"/> coalesces frames: moving the
    /// pointer across a toolbar can fire this many times per frame, and one Post per event would
    /// flood the dispatcher for cursors that are already obsolete by the time they are applied.
    /// </summary>
    private void OnCursorChanged(RemoteCursorDescriptor cursor)
    {
        if (IsDisposeStarted) return;

        lock (_cursorLock)
        {
            _pendingCursor = cursor;
        }

        if (Interlocked.Exchange(ref _cursorApplyQueued, 1) == 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyPendingCursor);
        }
    }

    private void ApplyPendingCursor()
    {
        Interlocked.Exchange(ref _cursorApplyQueued, 0);

        RemoteCursorDescriptor? descriptor;
        lock (_cursorLock)
        {
            descriptor = _pendingCursor;
            _pendingCursor = null;
        }

        if (descriptor is not { } cursor || IsDisposeStarted) return;

        // A null resolve means the shape could not be produced; keep showing the current cursor
        // rather than flashing back to the default arrow.
        var resolved = _cursorCache.Resolve(cursor.Kind, cursor.CursorId, cursor.Width, cursor.Height, cursor.HotX, cursor.HotY);
        if (resolved != null)
        {
            RemoteCursor = resolved;
        }
    }

    private bool CopyCursorImage(uint cursorId, IntPtr dest, int destStride, int destWidth, int destHeight)
    {
        return _runtime.TryGetActiveSession(out var nativeSession) &&
            nativeSession.CopyCursorImage(cursorId, dest, destStride, destWidth, destHeight);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _framePresenter.Dispose();

        // Clear the binding before disposing the cache: the viewport may still be showing one of
        // these cursors, and disposing a Cursor releases its platform handle.
        RemoteCursor = null;
        lock (_cursorLock)
        {
            _pendingCursor = null;
        }
        _cursorCache.Dispose();

        _runtime.Dispose();
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }


    private bool IsDisposeStarted => Volatile.Read(ref _disposeStarted) != 0;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

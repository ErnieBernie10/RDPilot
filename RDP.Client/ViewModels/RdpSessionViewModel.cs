using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using RDP.Client.Models;

namespace RDP.Client.ViewModels;

public partial class RdpSessionViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private WriteableBitmap? _screen;
    [ObservableProperty] private string _status = "Connecting";

    private readonly NativeWrapper.FrameCallback _frameCallback;
    private readonly NativeWrapper.ClipboardTextCallback _clipboardCallback;
    private readonly NativeWrapper.StatusCallback _statusCallback;
    private readonly Action<RdpSessionViewModel, string> _remoteClipboardTextReceived;
    private readonly object _frameLock = new();
    private IntPtr _handle;
    private int _requestedWidth;
    private int _requestedHeight;
    private byte[]? _pendingFrame;
    private int _pendingFrameSize;
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;
    private long _pendingFrameReceivedTicks;
    private bool _renderQueued;
    private long _lastPerfLogTicks = Stopwatch.GetTimestamp();
    private long _framesReceived;
    private long _framesRendered;
    private long _framesDropped;
    private long _bytesReceived;
    private double _queueDelayTotalMs;
    private double _queueDelayMaxMs;
    private long _lastInputTicks;
    private int _inputWaitingForFrame;
    private double _lastInputToFrameMs;
    private double _inputToFrameMaxMs;

    public RdpSessionViewModel(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
    {
        Connection = connection.Clone();
        Title = connection.Name;
        _requestedWidth = width;
        _requestedHeight = height;
        _remoteClipboardTextReceived = remoteClipboardTextReceived;
        _frameCallback = OnFrameReceived;
        _clipboardCallback = OnRemoteClipboardTextReceived;
        _statusCallback = OnStatusChanged;

        Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
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
            _statusCallback);

        Status = _handle == IntPtr.Zero ? "Failed" : "Connecting";
    }

    public SavedConnection Connection { get; }
    public string Title { get; }
    public bool IsConnected => _handle != IntPtr.Zero && Status == "Connected";
    public bool CanDisconnect => Status is "Connecting" or "Connected";
    public bool CanReconnect => Status is "Failed" or "Disconnected";
    public event EventHandler? RequestRedraw;

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanReconnect));
    }

    public async Task DisconnectAsync()
    {
        if (_handle == IntPtr.Zero)
        {
            Status = "Disconnected";
            return;
        }

        Status = "Disconnecting";
        await Task.Run(() => NativeWrapper.rdp_session_disconnect(_handle));
        Status = "Disconnected";
    }

    public void UpdateResolution(int width, int height)
    {
        if (_handle == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        _requestedWidth = width;
        _requestedHeight = height;
        NativeWrapper.rdp_session_update_resolution(_handle, width, height);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        if (_handle == IntPtr.Zero) return;
        MarkInputSent();
        NativeWrapper.rdp_session_send_mouse_event(_handle, flags, x, y);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        if (_handle == IntPtr.Zero) return;
        MarkInputSent();
        NativeWrapper.rdp_session_send_keyboard_event(_handle, flags, code);
    }

    public void SetLocalClipboardText(string text)
    {
        if (_handle == IntPtr.Zero) return;
        NativeWrapper.rdp_session_clipboard_set_local_text(_handle, text);
    }

    private void OnRemoteClipboardTextReceived(IntPtr session, IntPtr textPtr)
    {
        if (session != _handle) return;
        var text = Marshal.PtrToStringUTF8(textPtr) ?? "";
        _remoteClipboardTextReceived(this, text);
    }

    private void OnStatusChanged(IntPtr session, int status)
    {
        if (_handle != IntPtr.Zero && session != _handle) return;

        var statusText = status switch
        {
            1 => "Connected",
            2 => "Failed",
            3 => "Disconnected",
            _ => Status
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = statusText);
    }

    private void OnFrameReceived(IntPtr session, IntPtr data, int width, int height)
    {
        if (session != _handle) return;

        var size = width * height * 4;
        var frame = ArrayPool<byte>.Shared.Rent(size);
        Marshal.Copy(data, frame, 0, size);
        var receivedTicks = Stopwatch.GetTimestamp();
        var shouldPostRender = false;

        lock (_frameLock)
        {
            if (_pendingFrame != null)
            {
                ArrayPool<byte>.Shared.Return(_pendingFrame);
                _framesDropped++;
            }

            _pendingFrame = frame;
            _pendingFrameSize = size;
            _pendingFrameWidth = width;
            _pendingFrameHeight = height;
            _pendingFrameReceivedTicks = receivedTicks;
            _framesReceived++;
            _bytesReceived += size;

            if (!_renderQueued)
            {
                _renderQueued = true;
                shouldPostRender = true;
            }
        }

        if (shouldPostRender)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderPendingFrame);
        }
    }

    private void RenderPendingFrame()
    {
        byte[]? frame;
        int size;
        int width;
        int height;
        long receivedTicks;

        lock (_frameLock)
        {
            frame = _pendingFrame;
            size = _pendingFrameSize;
            width = _pendingFrameWidth;
            height = _pendingFrameHeight;
            receivedTicks = _pendingFrameReceivedTicks;
            _pendingFrame = null;
        }

        if (frame == null)
        {
            lock (_frameLock)
            {
                _renderQueued = false;
            }
            return;
        }

        try
        {
            var renderTicks = Stopwatch.GetTimestamp();
            var queueDelayMs = ElapsedMilliseconds(receivedTicks, renderTicks);

            if (Screen == null || Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
            {
                Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var lockedBitmap = Screen.Lock())
            {
                unsafe
                {
                    fixed (byte* framePtr = frame)
                    {
                        Buffer.MemoryCopy(framePtr, lockedBitmap.Address.ToPointer(), size, size);
                    }
                }
            }

            if (Interlocked.Exchange(ref _inputWaitingForFrame, 0) == 1)
            {
                var inputTicks = Interlocked.Read(ref _lastInputTicks);
                if (inputTicks != 0)
                {
                    _lastInputToFrameMs = ElapsedMilliseconds(inputTicks, renderTicks);
                    if (_lastInputToFrameMs > _inputToFrameMaxMs) _inputToFrameMaxMs = _lastInputToFrameMs;
                }
            }

            _framesRendered++;
            _queueDelayTotalMs += queueDelayMs;
            if (queueDelayMs > _queueDelayMaxMs) _queueDelayMaxMs = queueDelayMs;
            RequestRedraw?.Invoke(this, EventArgs.Empty);
            LogManagedPerfIfDue(renderTicks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }

        var shouldPostAgain = false;
        lock (_frameLock)
        {
            if (_pendingFrame != null) shouldPostAgain = true;
            else _renderQueued = false;
        }

        if (shouldPostAgain)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderPendingFrame);
        }
    }

    private void MarkInputSent()
    {
        Interlocked.Exchange(ref _lastInputTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _inputWaitingForFrame, 1);
    }

    private void LogManagedPerfIfDue(long nowTicks)
    {
        var elapsedMs = ElapsedMilliseconds(_lastPerfLogTicks, nowTicks);
        if (elapsedMs < 1000) return;

        var seconds = elapsedMs / 1000.0;
        var avgQueueMs = _framesRendered > 0 ? _queueDelayTotalMs / _framesRendered : 0.0;
        Console.WriteLine(
            $"[PERF_UI] session={Title} recv={_framesReceived / seconds:F1}/s render={_framesRendered / seconds:F1}/s drop={_framesDropped / seconds:F1}/s managedCopy={_bytesReceived / 1048576.0 / seconds:F1} MiB/s queueAvg={avgQueueMs:F1}ms queueMax={_queueDelayMaxMs:F1}ms inputNextFrame={_lastInputToFrameMs:F1}ms inputMax={_inputToFrameMaxMs:F1}ms");

        _lastPerfLogTicks = nowTicks;
        _framesReceived = 0;
        _framesRendered = 0;
        _framesDropped = 0;
        _bytesReceived = 0;
        _queueDelayTotalMs = 0;
        _queueDelayMaxMs = 0;
        _inputToFrameMaxMs = 0;
    }

    private static double ElapsedMilliseconds(long startTicks, long endTicks)
    {
        return (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeWrapper.rdp_session_free(_handle);
            _handle = IntPtr.Zero;
        }

        lock (_frameLock)
        {
            if (_pendingFrame != null)
            {
                ArrayPool<byte>.Shared.Return(_pendingFrame);
                _pendingFrame = null;
            }
        }
    }
}

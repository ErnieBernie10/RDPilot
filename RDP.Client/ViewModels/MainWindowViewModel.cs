using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RDP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private WriteableBitmap? _screen;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _gatewayHost = "";
    [ObservableProperty] private string _gatewayDomain = "";
    [ObservableProperty] private string _gatewayUsername = "";
    [ObservableProperty] private string _gatewayPassword = "";
    private readonly NativeWrapper.FrameCallback _frameCallback;
    private readonly object _frameLock = new();
    private int _requestedWidth = 1280;
    private int _requestedHeight = 720;
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

    public MainWindowViewModel()
    {
        _frameCallback = OnFrameReceived;
        NativeWrapper.set_frame_callback(_frameCallback);
        LoadLocalConnectionSettings();
    }

    private void LoadLocalConnectionSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "connection.local.json");
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var settings = JsonSerializer.Deserialize<LocalConnectionSettings>(File.ReadAllText(settingsPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (settings == null)
        {
            return;
        }

        Host = settings.Host ?? "";
        Domain = settings.Domain ?? "";
        Username = settings.Username ?? "";
        Password = settings.Password ?? "";
        GatewayHost = settings.GatewayHost ?? "";
        GatewayDomain = settings.GatewayDomain ?? "";
        GatewayUsername = settings.GatewayUsername ?? "";
        GatewayPassword = settings.GatewayPassword ?? "";
    }

    [RelayCommand]
    private void Connect()
    {
        int width = _requestedWidth;
        int height = _requestedHeight;

        if (Screen == null || Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
        {
            Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        NativeWrapper.connect_rdp(Host, Domain, Username, Password, GatewayHost, GatewayDomain, GatewayUsername, GatewayPassword, width, height);
    }

    [RelayCommand]
    private void Disconnect()
    {
        NativeWrapper.disconnect_rdp();
    }

    public void UpdateResolution(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _requestedWidth = width;
        _requestedHeight = height;
        NativeWrapper.update_resolution(width, height);
    }

    private void OnFrameReceived(IntPtr data, int width, int height)
    {
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
                Console.WriteLine($"[DEBUG_LOG] Resizing Screen to {width}x{height}");
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

            if (Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
            {
                OnPropertyChanged(nameof(Screen));
            }
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
            if (_pendingFrame != null)
            {
                shouldPostAgain = true;
            }
            else
            {
                _renderQueued = false;
            }
        }

        if (shouldPostAgain)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderPendingFrame);
        }
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        MarkInputSent();
        NativeWrapper.send_mouse_event(flags, x, y);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        MarkInputSent();
        NativeWrapper.send_keyboard_event(flags, code);
    }

    private void MarkInputSent()
    {
        Interlocked.Exchange(ref _lastInputTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _inputWaitingForFrame, 1);
    }

    private void LogManagedPerfIfDue(long nowTicks)
    {
        var elapsedMs = ElapsedMilliseconds(_lastPerfLogTicks, nowTicks);
        if (elapsedMs < 1000)
        {
            return;
        }

        var seconds = elapsedMs / 1000.0;
        var received = _framesReceived;
        var rendered = _framesRendered;
        var dropped = _framesDropped;
        var bytes = _bytesReceived;
        var avgQueueMs = rendered > 0 ? _queueDelayTotalMs / rendered : 0.0;

        Console.WriteLine(
            $"[PERF_UI] recv={received / seconds:F1}/s render={rendered / seconds:F1}/s drop={dropped / seconds:F1}/s managedCopy={bytes / 1048576.0 / seconds:F1} MiB/s queueAvg={avgQueueMs:F1}ms queueMax={_queueDelayMaxMs:F1}ms inputNextFrame={_lastInputToFrameMs:F1}ms inputMax={_inputToFrameMaxMs:F1}ms");

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

    public event EventHandler? RequestRedraw;

    private sealed class LocalConnectionSettings
    {
        public string? Host { get; set; }
        public string? Domain { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? GatewayHost { get; set; }
        public string? GatewayDomain { get; set; }
        public string? GatewayUsername { get; set; }
        public string? GatewayPassword { get; set; }
    }
}

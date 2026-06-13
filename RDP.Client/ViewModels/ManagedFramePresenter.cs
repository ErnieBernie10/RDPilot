using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RDP.Client.ViewModels;

internal sealed class ManagedFramePresenter : IDisposable
{
    private readonly object _frameLock = new();
    private readonly string _sessionTitle;
    private readonly Action<WriteableBitmap?> _setScreen;
    private readonly Action _requestRedraw;
    private int _disposed;
    private WriteableBitmap? _screen;
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

    public ManagedFramePresenter(string sessionTitle, int width, int height, Action<WriteableBitmap?> setScreen, Action requestRedraw, bool initializeBitmap = true)
    {
        _sessionTitle = sessionTitle;
        _setScreen = setScreen;
        _requestRedraw = requestRedraw;
        if (initializeBitmap)
        {
            _screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _setScreen(_screen);
        }
    }

    public void EnqueueFrame(IntPtr data, int width, int height)
    {
        if (IsDisposed) return;

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

    public void MarkInputSent()
    {
        Interlocked.Exchange(ref _lastInputTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _inputWaitingForFrame, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClearPendingFrame();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void RenderPendingFrame()
    {
        if (IsDisposed)
        {
            ClearPendingFrame();
            return;
        }

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
            if (IsDisposed)
            {
                return;
            }

            var renderTicks = Stopwatch.GetTimestamp();
            var queueDelayMs = ElapsedMilliseconds(receivedTicks, renderTicks);

            if (_screen == null || _screen.PixelSize.Width != width || _screen.PixelSize.Height != height)
            {
                _screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                _setScreen(_screen);
            }

            using (var lockedBitmap = _screen.Lock())
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
            _requestRedraw();
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

        if (shouldPostAgain && !IsDisposed)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderPendingFrame);
        }
    }

    private void LogManagedPerfIfDue(long nowTicks)
    {
        var elapsedMs = ElapsedMilliseconds(_lastPerfLogTicks, nowTicks);
        if (elapsedMs < 1000) return;

        var seconds = elapsedMs / 1000.0;
        var avgQueueMs = _framesRendered > 0 ? _queueDelayTotalMs / _framesRendered : 0.0;
        Console.WriteLine(
            $"[PERF_UI] session={_sessionTitle} recv={_framesReceived / seconds:F1}/s render={_framesRendered / seconds:F1}/s drop={_framesDropped / seconds:F1}/s managedCopy={_bytesReceived / 1048576.0 / seconds:F1} MiB/s queueAvg={avgQueueMs:F1}ms queueMax={_queueDelayMaxMs:F1}ms inputNextFrame={_lastInputToFrameMs:F1}ms inputMax={_inputToFrameMaxMs:F1}ms");

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

    private void ClearPendingFrame()
    {
        lock (_frameLock)
        {
            if (_pendingFrame != null)
            {
                ArrayPool<byte>.Shared.Return(_pendingFrame);
                _pendingFrame = null;
            }

            _renderQueued = false;
        }
    }
}

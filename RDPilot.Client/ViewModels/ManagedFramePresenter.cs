using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RDPilot.Client.ViewModels;

internal sealed class ManagedFramePresenter : IDisposable
{
    private readonly object _frameLock = new();
    private readonly string _sessionTitle;
    private readonly Action<WriteableBitmap?> _setScreen;
    private readonly Action _requestRedraw;
    private readonly PresentDelegate _present;
    private int _disposed;
    private WriteableBitmap? _screen;
    private int _presentQueued;
    private int _pendingCount;
    private double _renderScaling = 1.0;
    private long _lastPerfLogTicks = Stopwatch.GetTimestamp();
    private long _framesReceived;
    private long _framesPresented;
    private long _framesDropped;
    private long _bytesCopied;
    private double _copyTotalMs;
    private double _copyMaxMs;
    private double _queueDelayTotalMs;
    private double _queueDelayMaxMs;
    private long _lastInputTicks;
    private int _inputWaitingForFrame;
    private double _lastInputToFrameMs;
    private double _inputToFrameMaxMs;
    private long _lastReceivedTicks;

    public delegate bool PresentDelegate(IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight);

    public ManagedFramePresenter(string sessionTitle, int width, int height, Action<WriteableBitmap?> setScreen, Action requestRedraw, PresentDelegate present, double renderScaling = 1.0, bool initializeBitmap = true)
    {
        _sessionTitle = sessionTitle;
        _setScreen = setScreen;
        _requestRedraw = requestRedraw;
        _present = present;
        _renderScaling = renderScaling > 0 ? renderScaling : 1.0;
        if (initializeBitmap)
        {
            _screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _setScreen(_screen);
        }
    }

    public void UpdateRenderScaling(double scaling)
    {
        if (scaling > 0) _renderScaling = scaling;
    }

    public void EnqueueFrame(int width, int height)
    {
        if (IsDisposed || width <= 0 || height <= 0) return;

        bool shouldPost;
        lock (_frameLock)
        {
            _framesReceived++;
            // Track the latest received frame dims so the present loop can detect resize even
            // before the native call returns the actual GDI dims. The native present call is
            // authoritative, but this avoids a needless copy into a too-small bitmap.
            _pendingCount++;
            _lastReceivedTicks = Stopwatch.GetTimestamp();
            shouldPost = Interlocked.Exchange(ref _presentQueued, 1) == 0;
        }

        if (shouldPost)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Present);
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

        lock (_frameLock)
        {
            _pendingCount = 0;
        }
        Interlocked.Exchange(ref _presentQueued, 0);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void Present()
    {
        if (IsDisposed)
        {
            Interlocked.Exchange(ref _presentQueued, 0);
            return;
        }

        int pendingSnapshot;
        long receivedTicks;
        lock (_frameLock)
        {
            pendingSnapshot = _pendingCount;
            if (pendingSnapshot == 0)
            {
                Interlocked.Exchange(ref _presentQueued, 0);
                return;
            }
            _pendingCount = 0;
            receivedTicks = _lastReceivedTicks;
            // Multiple received frames are coalesced into a single present (the native side
            // merges their dirty rects under frame_lock). Count the intermediate frames as
            // dropped to surface coalescing pressure.
            if (pendingSnapshot > 1)
            {
                _framesDropped += pendingSnapshot - 1;
            }
        }

        var renderTicks = Stopwatch.GetTimestamp();
        var queueMs = ElapsedMilliseconds(receivedTicks, renderTicks);
        long copiedBytes = 0;
        double copyMs = 0;
        bool presented = false;

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (IsDisposed) break;

                var screen = _screen;

                // Bitmap was invalidated by UpdateRenderScaling or initial creation. Query native
                // for the current framebuffer dims and create a fresh bitmap at the right size
                // and DPI so the next attempt can copy into it.
                if (screen == null)
                {
                    _present(IntPtr.Zero, 0, 0, 0, out _, out _, out _, out _, out int fbW, out int fbH);
                    if (fbW <= 0 || fbH <= 0) break;
                    screen = new WriteableBitmap(new PixelSize(fbW, fbH), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _screen = screen;
                    _setScreen(screen);
                }

                PixelSize screenDims = screen.PixelSize;
                ILockedFramebuffer locked;
                try
                {
                    locked = screen.Lock();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }

                bool recreate = false;
                int newFbW = 0;
                int newFbH = 0;

                try
                {
                    long presentStart = Stopwatch.GetTimestamp();
                    bool ok = _present(
                        locked.Address,
                        locked.RowBytes,
                        screenDims.Width,
                        screenDims.Height,
                        out int dx,
                        out int dy,
                        out int dw,
                        out int dh,
                        out int fbW,
                        out int fbH);
                    long presentEnd = Stopwatch.GetTimestamp();

                    if (!ok)
                    {
                        if (fbW > 0 && fbH > 0 && (fbW != screenDims.Width || fbH != screenDims.Height))
                        {
                            recreate = true;
                            newFbW = fbW;
                            newFbH = fbH;
                        }
                    }
                    else
                    {
                        copyMs = ElapsedMilliseconds(presentStart, presentEnd);
                        copiedBytes = (long)dw * dh * 4;
                        presented = true;
                    }
                }
                finally
                {
                    locked.Dispose();
                }

                if (presented) break;

                if (recreate && !IsDisposed)
                {
                    Interlocked.Exchange(ref _screen, null);
                    try { screen.Dispose(); } catch { }
                    var fresh = new WriteableBitmap(new PixelSize(newFbW, newFbH), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _screen = fresh;
                    _setScreen(fresh);
                    continue;
                }

                break;
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            presented = false;
        }

        if (presented)
        {
            _framesPresented++;
            _bytesCopied += copiedBytes;
            _copyTotalMs += copyMs;
            if (copyMs > _copyMaxMs) _copyMaxMs = copyMs;
            _queueDelayTotalMs += queueMs;
            if (queueMs > _queueDelayMaxMs) _queueDelayMaxMs = queueMs;

            if (Interlocked.Exchange(ref _inputWaitingForFrame, 0) == 1)
            {
                var inputTicks = Interlocked.Read(ref _lastInputTicks);
                if (inputTicks != 0)
                {
                    _lastInputToFrameMs = ElapsedMilliseconds(inputTicks, renderTicks);
                    if (_lastInputToFrameMs > _inputToFrameMaxMs) _inputToFrameMaxMs = _lastInputToFrameMs;
                }
            }

            _requestRedraw();
        }

        LogManagedPerfIfDue(Stopwatch.GetTimestamp());

        bool morePending;
        lock (_frameLock)
        {
            morePending = _pendingCount > 0;
            if (!morePending)
            {
                Interlocked.Exchange(ref _presentQueued, 0);
            }
        }

        if (morePending && !IsDisposed)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Present);
        }
    }

    private void LogManagedPerfIfDue(long nowTicks)
    {
        var elapsedMs = ElapsedMilliseconds(_lastPerfLogTicks, nowTicks);
        if (elapsedMs < 1000) return;

        var seconds = elapsedMs / 1000.0;
        var avgQueueMs = _framesPresented > 0 ? _queueDelayTotalMs / _framesPresented : 0.0;
        var avgCopyMs = _framesPresented > 0 ? _copyTotalMs / _framesPresented : 0.0;
        Console.WriteLine(
            $"[PERF_UI] session={_sessionTitle} recv={_framesReceived / seconds:F1}/s present={_framesPresented / seconds:F1}/s drop={_framesDropped / seconds:F1}/s copy={_bytesCopied / 1048576.0 / seconds:F1} MiB/s queueAvg={avgQueueMs:F1}ms queueMax={_queueDelayMaxMs:F1}ms copyAvg={avgCopyMs:F2}ms copyMax={_copyMaxMs:F2}ms inputNextFrame={_lastInputToFrameMs:F1}ms inputMax={_inputToFrameMaxMs:F1}ms");

        _lastPerfLogTicks = nowTicks;
        _framesReceived = 0;
        _framesPresented = 0;
        _framesDropped = 0;
        _bytesCopied = 0;
        _copyTotalMs = 0;
        _copyMaxMs = 0;
        _queueDelayTotalMs = 0;
        _queueDelayMaxMs = 0;
        _inputToFrameMaxMs = 0;
    }

    private static double ElapsedMilliseconds(long startTicks, long endTicks)
    {
        return (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
    }
}
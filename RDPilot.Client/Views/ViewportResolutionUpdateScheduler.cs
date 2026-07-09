using System;
using System.Threading;
using System.Threading.Tasks;

namespace RDPilot.Client.Views;

internal sealed class ViewportResolutionUpdateScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly TimeSpan _quietDelay;
    private readonly TimeSpan _minimumInterval;
    private CancellationTokenSource? _pendingCts;
    private DateTimeOffset _lastSentAtUtc = DateTimeOffset.MinValue;
    private PendingResolution? _pending;

    public ViewportResolutionUpdateScheduler(
        Action<Action>? post = null,
        TimeSpan? quietDelay = null,
        TimeSpan? minimumInterval = null)
    {
        _post = post ?? (action => action());
        _quietDelay = quietDelay ?? TimeSpan.FromMilliseconds(1000);
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(1500);
    }

    public void Schedule(int width, int height, double renderScaling, Action<int, int, double> applyResolution)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            var next = new PendingResolution(width, height, renderScaling, applyResolution);
            if (_pending is { } pending && pending.Equals(next))
            {
                return;
            }

            _pending = next;
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            cts = _pendingCts;
        }

        _ = RunPendingAsync(cts);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending = null;
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    public void Dispose()
    {
        Cancel();
    }

    private async Task RunPendingAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_quietDelay, cts.Token);

            TimeSpan remainingDelay;
            PendingResolution pending;
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingCts, cts) || _pending is null)
                {
                    return;
                }

                pending = _pending;
                var earliestNextSend = _lastSentAtUtc + _minimumInterval;
                remainingDelay = earliestNextSend > DateTimeOffset.UtcNow
                    ? earliestNextSend - DateTimeOffset.UtcNow
                    : TimeSpan.Zero;
            }

            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cts.Token);
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_pendingCts, cts) || _pending is null || !_pending.Equals(pending))
                {
                    return;
                }

                _pending = null;
                _pendingCts = null;
                _lastSentAtUtc = DateTimeOffset.UtcNow;
            }

            _post(() => pending.ApplyResolution(pending.Width, pending.Height, pending.RenderScaling));
            cts.Dispose();
        }
        catch (OperationCanceledException)
        {
            cts.Dispose();
        }
    }

    private sealed record PendingResolution(int Width, int Height, double RenderScaling, Action<int, int, double> ApplyResolution);
}

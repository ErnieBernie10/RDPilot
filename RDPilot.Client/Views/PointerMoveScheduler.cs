using System;
using System.Threading.Tasks;

namespace RDPilot.Client.Views;

internal sealed class PointerMoveScheduler
{
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly TimeSpan _minimumInterval;
    private PendingMove? _pendingMove;
    private bool _workerRunning;
    private DateTimeOffset _lastSentAtUtc = DateTimeOffset.MinValue;

    public PointerMoveScheduler(Action<Action>? post = null, TimeSpan? minimumInterval = null)
    {
        _post = post ?? (action => action());
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(8);
    }

    public void Schedule(double dipX, double dipY, Action<double, double> sendMove)
    {
        lock (_gate)
        {
            _pendingMove = new PendingMove(dipX, dipY, sendMove);
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        _ = RunAsync();
    }

    public void Flush()
    {
        PendingMove? pending;
        lock (_gate)
        {
            pending = _pendingMove;
            _pendingMove = null;
            if (pending is not null)
            {
                _lastSentAtUtc = DateTimeOffset.UtcNow;
            }
        }

        if (pending is null)
        {
            return;
        }

        _post(() => pending.SendMove(pending.DipX, pending.DipY));
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pendingMove = null;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            PendingMove? pending;
            TimeSpan delay;
            lock (_gate)
            {
                pending = _pendingMove;
                if (pending is null)
                {
                    _workerRunning = false;
                    return;
                }

                var earliestNextSend = _lastSentAtUtc + _minimumInterval;
                delay = earliestNextSend > DateTimeOffset.UtcNow
                    ? earliestNextSend - DateTimeOffset.UtcNow
                    : TimeSpan.Zero;

                if (delay == TimeSpan.Zero)
                {
                    _pendingMove = null;
                    _lastSentAtUtc = DateTimeOffset.UtcNow;
                }
            }

            if (pending is null)
            {
                continue;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
                continue;
            }

            _post(() => pending.SendMove(pending.DipX, pending.DipY));
        }
    }

    private sealed record PendingMove(double DipX, double DipY, Action<double, double> SendMove);
}

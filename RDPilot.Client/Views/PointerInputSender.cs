using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

internal sealed class PointerInputSender
{
    private readonly object _gate = new();
    private readonly Func<RdpSessionViewModel?> _getTarget;
    private readonly Action<Action> _post;
    private readonly TimeSpan _minimumInterval;
    private Point? _pendingMove;
    private bool _drainScheduled;
    private DateTimeOffset _lastMoveSentAtUtc = DateTimeOffset.MinValue;

    public PointerInputSender(Func<RdpSessionViewModel?> getTarget, Action<Action>? post = null, TimeSpan? minimumInterval = null)
    {
        _getTarget = getTarget ?? throw new ArgumentNullException(nameof(getTarget));
        _post = post ?? (action => action());
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(8);
    }

    public void Move(double dipX, double dipY)
    {
        lock (_gate)
        {
            _pendingMove = new Point(dipX, dipY);
            if (_drainScheduled)
            {
                return;
            }

            _drainScheduled = true;
        }

        _ = ScheduleDrainAsync();
    }

    public void Press(double dipX, double dipY, PointerPointProperties properties)
    {
        ushort flags = RdpPointerInputMapper.PointerDownFlag;
        if (properties.IsLeftButtonPressed) flags |= RdpPointerInputMapper.PointerButton1Flag;
        if (properties.IsRightButtonPressed) flags |= RdpPointerInputMapper.PointerButton2Flag;
        if (properties.IsMiddleButtonPressed) flags |= RdpPointerInputMapper.PointerButton3Flag;
        SendPendingMove();
        Send(flags, dipX, dipY);
    }

    public void Release(double dipX, double dipY, PointerUpdateKind updateKind)
    {
        ushort flags = updateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => RdpPointerInputMapper.PointerButton1Flag,
            PointerUpdateKind.RightButtonReleased => RdpPointerInputMapper.PointerButton2Flag,
            PointerUpdateKind.MiddleButtonReleased => RdpPointerInputMapper.PointerButton3Flag,
            _ => 0
        };
        SendPendingMove();
        Send(flags, dipX, dipY);
    }

    public void Wheel(double dipX, double dipY, Vector delta)
    {
        SendPendingMove();
        SendWheel(RdpPointerInputMapper.PointerWheelFlag, delta.Y, dipX, dipY);
        SendWheel(RdpPointerInputMapper.PointerHorizontalWheelFlag, delta.X, dipX, dipY);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pendingMove = null;
        }
    }

    private async Task ScheduleDrainAsync()
    {
        TimeSpan delay;
        lock (_gate)
        {
            delay = _lastMoveSentAtUtc + _minimumInterval - DateTimeOffset.UtcNow;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        _post(DrainMove);
    }

    private void DrainMove()
    {
        lock (_gate)
        {
            _drainScheduled = false;
        }

        SendPendingMove();
    }

    private void SendPendingMove()
    {
        Point? move;
        lock (_gate)
        {
            move = _pendingMove;
            _pendingMove = null;
            if (move is not null)
            {
                _lastMoveSentAtUtc = DateTimeOffset.UtcNow;
            }
        }

        if (move is { } position)
        {
            Send(RdpPointerInputMapper.PointerMoveFlag, position.X, position.Y);
        }
    }

    private void SendWheel(ushort wheelFlag, double delta, double dipX, double dipY)
    {
        var flags = RdpPointerInputMapper.BuildWheelFlags(wheelFlag, delta);
        if (flags != 0)
        {
            Send(flags, dipX, dipY);
        }
    }

    private void Send(ushort flags, double dipX, double dipY)
    {
        var target = _getTarget();
        if (target == null)
        {
            return;
        }

        var scaling = target.RenderScaling;
        target.Send(new PointerInput(flags, ToDesktopPixel(dipX, scaling), ToDesktopPixel(dipY, scaling)));
    }

    private static ushort ToDesktopPixel(double dip, double scaling) => (ushort)Math.Clamp(dip * scaling, 0, 65535);
}

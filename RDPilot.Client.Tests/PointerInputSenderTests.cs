using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class PointerInputSenderTests
{
    private const ushort Move = 0x0800;

    [Fact]
    public async Task Move_CoalescesPendingMovesToLatestPosition()
    {
        using var context = new SessionTestContext();
        var sender = new PointerInputSender(() => context.ViewModel, minimumInterval: TimeSpan.FromMilliseconds(40));

        sender.Move(10, 20);
        await Task.Delay(5);
        sender.Move(30, 40);
        sender.Move(50, 60);

        await Task.Delay(100);

        Assert.Equal([(Move, (ushort)10, (ushort)20), (Move, (ushort)50, (ushort)60)], context.Native.MouseEvents);
    }

    [Fact]
    public async Task Move_RespectsMinimumIntervalBetweenMoveSends()
    {
        var sentAt = new List<long>();
        var sender = new PointerInputSender(() => null, action =>
        {
            sentAt.Add(Stopwatch.GetTimestamp());
            action();
        }, TimeSpan.FromMilliseconds(40));

        sender.Move(10, 20);
        await Task.Delay(5);
        sender.Move(30, 40);

        await Task.Delay(150);

        Assert.True(sentAt.Count >= 2);
        Assert.True(Stopwatch.GetElapsedTime(sentAt[0], sentAt[1]) >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void Press_SendsPendingMoveSynchronouslyBeforeTheButton()
    {
        using var context = new SessionTestContext();
        var posted = new Queue<Action>();
        var sender = new PointerInputSender(() => context.ViewModel, posted.Enqueue, TimeSpan.FromSeconds(10));

        sender.Move(10, 20);
        sender.Move(30, 40);
        sender.Press(30, 40, new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed));
        while (posted.TryDequeue(out var action)) action();

        Assert.Equal([(Move, (ushort)30, (ushort)40), ((ushort)0xA000, (ushort)30, (ushort)40)], context.Native.MouseEvents);
    }

    [Fact]
    public void Wheel_SendsVerticalThenHorizontalWithSignedDeltas()
    {
        using var context = new SessionTestContext();
        var sender = new PointerInputSender(() => context.ViewModel);

        sender.Wheel(1, 2, new Vector(0.5, -1));

        Assert.Equal([((ushort)0x0378, (ushort)1, (ushort)2), ((ushort)0x043C, (ushort)1, (ushort)2)], context.Native.MouseEvents);
    }

    [Fact]
    public void Cancel_DropsThePendingMove()
    {
        using var context = new SessionTestContext();
        var posted = new Queue<Action>();
        var sender = new PointerInputSender(() => context.ViewModel, posted.Enqueue);

        sender.Move(10, 20);
        sender.Cancel();
        sender.Release(10, 20, PointerUpdateKind.LeftButtonReleased);
        while (posted.TryDequeue(out var action)) action();

        Assert.Equal([((ushort)0x1000, (ushort)10, (ushort)20)], context.Native.MouseEvents);
    }

    [Fact]
    public void Send_ScalesDipsToDesktopPixelsWithTheTargetRenderScaling()
    {
        using var context = new SessionTestContext(scaling: 1.5);
        var sender = new PointerInputSender(() => context.ViewModel);

        sender.Press(10.4, 20.8, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed));

        Assert.Equal(((ushort)0x9000, (ushort)15, (ushort)31), Assert.Single(context.Native.MouseEvents));
    }
}

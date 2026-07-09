using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class PointerMoveSchedulerTests
{
    [Fact]
    public async Task Schedule_CoalescesPendingMovesToLatestPosition()
    {
        var sent = new List<(double X, double Y)>();
        var scheduler = new PointerMoveScheduler(action => action(), TimeSpan.FromMilliseconds(40));

        scheduler.Schedule(10, 20, Capture);
        await Task.Delay(5);
        scheduler.Schedule(30, 40, Capture);
        scheduler.Schedule(50, 60, Capture);

        await Task.Delay(100);

        Assert.Equal(2, sent.Count);
        Assert.Equal((10d, 20d), sent[0]);
        Assert.Equal((50d, 60d), sent[1]);

        void Capture(double x, double y) => sent.Add((x, y));
    }

    [Fact]
    public async Task Schedule_RespectsMinimumIntervalBetweenMoveSends()
    {
        var sentAt = new List<DateTimeOffset>();
        var scheduler = new PointerMoveScheduler(action => action(), TimeSpan.FromMilliseconds(40));

        scheduler.Schedule(10, 20, Capture);
        await Task.Delay(5);
        scheduler.Schedule(30, 40, Capture);

        await Task.Delay(100);

        Assert.Equal(2, sentAt.Count);
        Assert.True(sentAt[1] - sentAt[0] >= TimeSpan.FromMilliseconds(30));

        void Capture(double _, double __) => sentAt.Add(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Flush_SendsPendingMoveImmediately()
    {
        var sent = new List<(double X, double Y)>();
        var scheduler = new PointerMoveScheduler(action => action(), TimeSpan.FromSeconds(1));

        scheduler.Schedule(10, 20, Capture);
        scheduler.Schedule(30, 40, Capture);
        scheduler.Flush();

        Assert.NotEmpty(sent);
        Assert.Equal((30d, 40d), sent[^1]);

        void Capture(double x, double y) => sent.Add((x, y));
    }
}

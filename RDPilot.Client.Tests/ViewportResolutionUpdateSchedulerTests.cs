using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ViewportResolutionUpdateSchedulerTests
{
    [Fact]
    public async Task Schedule_CoalescesRapidUpdatesToLatestResolution()
    {
        var applied = new List<(int Width, int Height, double Scale)>();
        using var scheduler = new ViewportResolutionUpdateScheduler(
            post: action => action(),
            quietDelay: TimeSpan.FromMilliseconds(25),
            minimumInterval: TimeSpan.FromMilliseconds(10));

        scheduler.Schedule(1000, 700, 1.0, Capture);
        scheduler.Schedule(1200, 800, 1.25, Capture);

        await Task.Delay(80);

        Assert.Equal([(1200, 800, 1.25)], applied);
        return;

        void Capture(int width, int height, double scale) => applied.Add((width, height, scale));
    }

    [Fact]
    public async Task Schedule_RespectsMinimumIntervalBetweenSends()
    {
        var appliedAt = new List<DateTimeOffset>();
        using var scheduler = new ViewportResolutionUpdateScheduler(
            post: action => action(),
            quietDelay: TimeSpan.FromMilliseconds(10),
            minimumInterval: TimeSpan.FromMilliseconds(60));

        scheduler.Schedule(1000, 700, 1.0, Capture);
        await Task.Delay(30);
        scheduler.Schedule(1200, 800, 1.0, Capture);

        await Task.Delay(130);

        Assert.Equal(2, appliedAt.Count);
        Assert.True(appliedAt[1] - appliedAt[0] >= TimeSpan.FromMilliseconds(50));
        return;

        void Capture(int _, int __, double ___) => appliedAt.Add(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Cancel_DropsPendingResolution()
    {
        var applied = new List<(int Width, int Height, double Scale)>();
        using var scheduler = new ViewportResolutionUpdateScheduler(
            post: action => action(),
            quietDelay: TimeSpan.FromMilliseconds(40),
            minimumInterval: TimeSpan.Zero);

        scheduler.Schedule(1000, 700, 1.0, Capture);
        scheduler.Cancel();

        await Task.Delay(80);

        Assert.Empty(applied);
        return;

        void Capture(int width, int height, double scale) => applied.Add((width, height, scale));
    }
}

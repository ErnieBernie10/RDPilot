using System.Diagnostics.CodeAnalysis;
using Avalonia;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ViewportResolutionServiceTests
{
    private readonly ViewportResolutionService _service = new();

    [Fact]
    public void TryCompute_ReturnsFalseWhenWindowIsMinimized()
    {
        var result = _service.TryCompute(new Size(1920, 1080), 1.0, isMinimized: true, out var width, out var height, out var scaling);

        Assert.False(result);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.Equal(1.0, scaling);
    }

    [Fact]
    public void TryCompute_ReturnsFalseWhenPhysicalSizeIsBelowMinimum()
    {
        var result = _service.TryCompute(new Size(639, 479), 1.0, isMinimized: false, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryCompute_ConvertsDipSizeToPhysicalPixels()
    {
        var result = _service.TryCompute(new Size(800.5, 600.5), 1.5, isMinimized: false, out var width, out var height, out var scaling);

        Assert.True(result);
        Assert.Equal(1200, width);
        Assert.Equal(900, height);
        Assert.Equal(1.5, scaling);
    }
}

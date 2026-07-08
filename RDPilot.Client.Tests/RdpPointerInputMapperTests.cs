using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpPointerInputMapperTests
{
    [Fact]
    public void BuildWheelFlags_ReturnsZeroForNoDelta()
    {
        Assert.Equal((ushort)0, RdpPointerInputMapper.BuildWheelFlags(RdpPointerInputMapper.PointerWheelFlag, 0));
    }

    [Theory]
    [InlineData(1.0, (ushort)0x0278)]
    [InlineData(-0.5, (ushort)0x033C)]
    public void BuildWheelFlags_EncodesMagnitudeAndDirection(double delta, ushort expected)
    {
        Assert.Equal(expected, RdpPointerInputMapper.BuildWheelFlags(RdpPointerInputMapper.PointerWheelFlag, delta));
    }

    [Fact]
    public void BuildWheelFlags_UsesMinimumMagnitudeOfOne()
    {
        Assert.Equal((ushort)0x0201, RdpPointerInputMapper.BuildWheelFlags(RdpPointerInputMapper.PointerWheelFlag, 0.001));
    }
}

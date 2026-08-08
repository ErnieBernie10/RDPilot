using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Models;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionOptionsTests
{
    [Theory]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(8, 16)]
    [InlineData(0, 16)]
    [InlineData(-1, 16)]
    [InlineData(15, 16)]
    [InlineData(48, 16)]
    public void NormalizeColorDepth_ClampsToValidValues(int input, int expected)
    {
        Assert.Equal(expected, RdpSessionOptions.NormalizeColorDepth(input));
    }

    [Theory]
    [InlineData(RdpConnectionType.Modem, (int)RdpConnectionType.Modem)]
    [InlineData(RdpConnectionType.Lan, (int)RdpConnectionType.Lan)]
    [InlineData(RdpConnectionType.Autodetect, (int)RdpConnectionType.Autodetect)]
    [InlineData((RdpConnectionType)0, (int)RdpConnectionType.Wan)]
    [InlineData((RdpConnectionType)99, (int)RdpConnectionType.Wan)]
    public void NormalizeConnectionType_ValidatesRange(RdpConnectionType input, int expected)
    {
        Assert.Equal(expected, RdpSessionOptions.NormalizeConnectionType(input));
    }

    [Theory]
    [InlineData(RdpConnectionType.Autodetect, 0, true)]
    [InlineData(RdpConnectionType.Wan, (int)RdpConnectionType.Wan, true)]
    [InlineData(RdpConnectionType.Lan, (int)RdpConnectionType.Lan, true)]
    [InlineData((RdpConnectionType)99, (int)RdpConnectionType.Wan, true)]
    public void NormalizeNetworkSettings_EnablesProtocolSupportIndependentlyOfQualityHint(
        RdpConnectionType input,
        int expectedConnectionType,
        bool expectedNetworkAutoDetect)
    {
        var actual = RdpSessionOptions.NormalizeNetworkSettings(input);

        Assert.Equal(expectedConnectionType, actual.ConnectionType);
        Assert.Equal(expectedNetworkAutoDetect, actual.NetworkAutoDetect);
    }

    [Theory]
    [InlineData(0u, 100u)]
    [InlineData(99u, 100u)]
    [InlineData(100u, 100u)]
    [InlineData(125u, 125u)]
    [InlineData(150u, 150u)]
    [InlineData(200u, 200u)]
    [InlineData(500u, 500u)]
    [InlineData(600u, 500u)]
    public void ClampDpiScalePercent_KeepsTheRealPercentageWithinTheDesktopRange(uint input, uint expected)
    {
        Assert.Equal(expected, RdpSessionOptions.ClampDpiScalePercent(input));
    }

    [Theory]
    [InlineData(100u, 100u)]
    [InlineData(119u, 100u)]
    [InlineData(120u, 140u)]
    [InlineData(125u, 140u)]
    [InlineData(150u, 140u)]
    [InlineData(159u, 140u)]
    [InlineData(160u, 180u)]
    [InlineData(200u, 180u)]
    [InlineData(500u, 180u)]
    public void ToDeviceScalePercent_SnapsToTheNearestLegalStep(uint input, uint expected)
    {
        Assert.Equal(expected, RdpSessionOptions.ToDeviceScalePercent(input));
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(125u)]
    [InlineData(150u)]
    [InlineData(500u)]
    public void ToDeviceScalePercent_OnlyEverProducesValuesTheServerAccepts(uint input)
    {
        // MS-RDPBCGR 2.2.1.3.2: an out-of-range deviceScaleFactor makes the server discard the
        // desktop factor as well, so this is the constraint that protects both values.
        Assert.Contains(RdpSessionOptions.ToDeviceScalePercent(input), new uint[] { 100, 140, 180 });
    }

    [Fact]
    public void NormalizeResolution_ClampsToDisplayControlLimits()
    {
        var (width, height) = RdpSessionOptions.NormalizeResolution(100, 100);
        Assert.Equal((200, 200), (width, height));
    }

    [Fact]
    public void NormalizeResolution_ClampsToMaxDisplaySize()
    {
        var (width, height) = RdpSessionOptions.NormalizeResolution(99999, 99999);
        Assert.Equal((8192, 8192), (width, height));
    }

    [Fact]
    public void NormalizeResolution_RoundsWidthToEvenNumber()
    {
        var (width, _) = RdpSessionOptions.NormalizeResolution(801, 600);
        Assert.Equal(800, width);
    }

    [Fact]
    public void NormalizeResolution_PassesValidEvenValuesThrough()
    {
        var (width, height) = RdpSessionOptions.NormalizeResolution(1920, 1080);
        Assert.Equal((1920, 1080), (width, height));
    }

    [Theory]
    [InlineData((ushort)0, (ushort)3389)]
    [InlineData((ushort)3389, (ushort)3389)]
    [InlineData((ushort)65535, (ushort)65535)]
    public void NormalizePort_UsesDefaultForInvalidPersistedValue(ushort input, ushort expected)
    {
        Assert.Equal(expected, RdpSessionOptions.NormalizePort(input));
    }
}

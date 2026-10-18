using System;
using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Models;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionStartRequestTests
{
    [Theory]
    [InlineData(1.5, 150u, 140u)]
    [InlineData(0, 100u, 100u)]
    [InlineData(100, 500u, 180u)]
    [InlineData(double.NaN, 100u, 100u)]
    public void Constructor_NormalizesConnectTimeDpi(double scaling, uint desktop, uint device)
    {
        var request = Create(scaling);

        Assert.Equal(desktop, request.DpiScalePercent);
        Assert.Equal(device, request.DeviceScalePercent);
    }

    [Fact]
    public void Constructor_ClonesProfileAndNormalizesInitialQualityAndResolution()
    {
        var profile = new SavedConnection { Name = "Original", Host = "example.local" };
        var request = new RdpSessionStartRequest(profile, "secret", "gateway-secret", 1601, 900, 1.5,
            new RdpEffectiveQualitySettings(8, true, false, false, false, false, true, true, RdpConnectionType.Wan));
        profile.Name = "Changed";

        Assert.Equal("Original", request.Connection.Name);
        Assert.Equal(1600, request.Width);
        Assert.Equal(900, request.Height);
        Assert.Equal(16, request.Quality.ColorDepth);
        Assert.Equal("secret", request.Password);
        Assert.Equal("gateway-secret", request.GatewayPassword);
        Assert.DoesNotContain("secret", request.ToString(), StringComparison.Ordinal);
    }

    private static RdpSessionStartRequest Create(double scaling) => new(
        new SavedConnection { Host = "example.local" }, "", "", 1280, 720, scaling,
        new RdpEffectiveQualitySettings(16, false, false, false, false, false, true, true, RdpConnectionType.Wan));
}

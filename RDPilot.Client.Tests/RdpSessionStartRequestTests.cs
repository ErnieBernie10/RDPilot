using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RDPilot.Client.Models;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpSessionStartRequestTests
{
    private static readonly RdpConnectEnvironment NoLookups =
        new(false, _ => throw new InvalidOperationException("DNS must not be queried."), () => 0);

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

    [Theory]
    [InlineData(1.5, 150u, 140u)]
    [InlineData(1.25, 125u, 140u)]
    [InlineData(0, 100u, 100u)]
    [InlineData(100, 500u, 180u)]
    [InlineData(double.NaN, 100u, 100u)]
    public void CreateConnectPlan_NormalizesConnectTimeDpi(double scaling, uint desktop, uint device)
    {
        var plan = Create(scaling: scaling).CreateConnectPlan(NoLookups);

        Assert.Equal(desktop, plan.DesktopScalePercent);
        Assert.Equal(device, plan.DeviceScalePercent);
    }

    [Fact]
    public void CreateConnectPlan_CopiesProfileCredentialsAndQuality()
    {
        var profile = new SavedConnection
        {
            Host = "rdp.example",
            Port = 3390,
            Domain = "CORP",
            Username = "alice",
            GatewayHost = "gw.example",
            GatewayDomain = "GW",
            GatewayUsername = "bob"
        };
        var request = new RdpSessionStartRequest(profile, "secret", "gateway-secret", 1601, 900, 1,
            new RdpEffectiveQualitySettings(24, true, true, false, true, false, false, true, RdpConnectionType.Lan));

        var plan = request.CreateConnectPlan(new RdpConnectEnvironment(false, _ => [], () => 0x0813));

        Assert.Equal("rdp.example", plan.Host);
        Assert.Equal("rdp.example", plan.ConnectHost);
        Assert.Equal((ushort)3390, plan.Port);
        Assert.Equal("CORP", plan.Domain);
        Assert.Equal("alice", plan.Username);
        Assert.Equal("secret", plan.Password);
        Assert.Equal("gw.example", plan.GatewayHost);
        Assert.Equal("GW", plan.GatewayDomain);
        Assert.Equal("bob", plan.GatewayUsername);
        Assert.Equal("gateway-secret", plan.GatewayPassword);
        Assert.Equal((1600, 900), (plan.Width, plan.Height));
        Assert.Equal(24, plan.ColorDepth);
        Assert.Equal((uint)RdpConnectionType.Lan, plan.ConnectionType);
        Assert.True(plan.NetworkAutoDetect);
        Assert.Equal(0x0813u, plan.KeyboardLayout);
        Assert.True(plan.FontSmoothing);
        Assert.True(plan.DesktopWallpaper);
        Assert.False(plan.Themes);
        Assert.True(plan.MenuAnimations);
        Assert.False(plan.FullWindowDrag);
        Assert.False(plan.Compression);
        Assert.True(plan.BitmapCache);
        Assert.DoesNotContain("secret", plan.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateConnectPlan_DefaultsAnUnsetPort()
    {
        var plan = Create(port: 0).CreateConnectPlan(NoLookups);

        Assert.Equal((ushort)3389, plan.Port);
    }

    [Fact]
    public void CreateConnectPlan_SendsNoQualityHintForAutodetect()
    {
        var plan = Create(connectionType: RdpConnectionType.Autodetect).CreateConnectPlan(NoLookups);

        Assert.Equal(0u, plan.ConnectionType);
        Assert.True(plan.NetworkAutoDetect);
    }

    [Theory]
    [InlineData("alice", "secret", true)]
    [InlineData("alice", "", false)]
    [InlineData("", "secret", false)]
    [InlineData(" ", " ", false)]
    public void CreateConnectPlan_UsesNlaOnlyWithCredentials(string username, string password, bool expected)
    {
        var plan = Create(username: username, password: password).CreateConnectPlan(NoLookups);

        Assert.Equal(expected, plan.UseNetworkLevelAuthentication);
    }

    [Fact]
    public void CreateConnectPlan_PrefersAnIPv4AddressWhenPreResolving()
    {
        var environment = new RdpConnectEnvironment(true,
            _ => [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("192.0.2.10")], () => 0);

        var plan = Create(host: "rdp.example").CreateConnectPlan(environment);

        Assert.Equal("rdp.example", plan.Host);
        Assert.Equal("192.0.2.10", plan.ConnectHost);
    }

    [Fact]
    public void CreateConnectPlan_FallsBackToIPv6WhenNoIPv4AddressResolves()
    {
        var environment = new RdpConnectEnvironment(true, _ => [IPAddress.Parse("2001:db8::1")], () => 0);

        var plan = Create(host: "rdp.example").CreateConnectPlan(environment);

        Assert.Equal("2001:db8::1", plan.ConnectHost);
    }

    [Fact]
    public void CreateConnectPlan_KeepsTheHostNameWhenResolutionFails()
    {
        var environment = new RdpConnectEnvironment(true, _ => throw new SocketException((int)SocketError.HostNotFound), () => 0);

        var plan = Create(host: "rdp.example").CreateConnectPlan(environment);

        Assert.Equal("rdp.example", plan.ConnectHost);
    }

    [Fact]
    public void CreateConnectPlan_KeepsTheHostNameWhenNothingResolves()
    {
        var environment = new RdpConnectEnvironment(true, _ => [], () => 0);

        var plan = Create(host: "rdp.example").CreateConnectPlan(environment);

        Assert.Equal("rdp.example", plan.ConnectHost);
    }

    [Fact]
    public void CreateConnectPlan_DoesNotResolveAnAddressLiteral()
    {
        var environment = NoLookups with { PreResolvesDirectConnectHost = true };

        var plan = Create(host: "192.0.2.10").CreateConnectPlan(environment);

        Assert.Equal("192.0.2.10", plan.ConnectHost);
    }

    [Fact]
    public void CreateConnectPlan_DoesNotResolveWhenThePlatformDoesNotPreResolve()
    {
        var plan = Create(host: "rdp.example").CreateConnectPlan(NoLookups);

        Assert.Equal("rdp.example", plan.ConnectHost);
    }

    private static RdpSessionStartRequest Create(
        double scaling = 1,
        string host = "example.local",
        ushort port = 3389,
        string username = "",
        string password = "",
        RdpConnectionType connectionType = RdpConnectionType.Wan) => new(
        new SavedConnection { Host = host, Port = port, Username = username }, password, "", 1280, 720, scaling,
        new RdpEffectiveQualitySettings(16, false, false, false, false, false, true, true, connectionType));
}

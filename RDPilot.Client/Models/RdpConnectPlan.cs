using System;
using System.Net;

namespace RDPilot.Client.Models;

internal sealed record RdpConnectEnvironment(
    bool PreResolvesDirectConnectHost,
    Func<string, IPAddress[]> GetHostAddresses,
    Func<uint> GetKeyboardLayout);

internal sealed class RdpConnectPlan
{
    public required string Host { get; init; }
    public required string ConnectHost { get; init; }
    public required ushort Port { get; init; }
    public required string Domain { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string GatewayHost { get; init; }
    public required string GatewayDomain { get; init; }
    public required string GatewayUsername { get; init; }
    public required string GatewayPassword { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int ColorDepth { get; init; }
    public required uint ConnectionType { get; init; }
    public required bool NetworkAutoDetect { get; init; }
    public required bool UseNetworkLevelAuthentication { get; init; }
    public required uint KeyboardLayout { get; init; }
    public required uint DesktopScalePercent { get; init; }
    public required uint DeviceScalePercent { get; init; }
    public required bool Compression { get; init; }
    public required bool BitmapCache { get; init; }
    public required bool FontSmoothing { get; init; }
    public required bool DesktopWallpaper { get; init; }
    public required bool Themes { get; init; }
    public required bool MenuAnimations { get; init; }
    public required bool FullWindowDrag { get; init; }
}

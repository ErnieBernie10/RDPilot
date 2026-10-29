using System;

namespace RDPilot.Client.Models;

/// <summary>Transient, normalized inputs for opening one RDP session. Credentials are never persisted here.</summary>
public sealed class RdpSessionStartRequest
{
    public RdpSessionStartRequest(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        double renderScaling,
        RdpEffectiveQualitySettings quality)
    {
        Connection = (connection ?? throw new ArgumentNullException(nameof(connection))).Clone();
        Password = password ?? throw new ArgumentNullException(nameof(password));
        GatewayPassword = gatewayPassword ?? throw new ArgumentNullException(nameof(gatewayPassword));
        (Width, Height) = RdpSessionOptions.NormalizeResolution(width, height);
        RenderScaling = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        Quality = quality with { ColorDepth = RdpSessionOptions.NormalizeColorDepth(quality.ColorDepth) };
    }

    public SavedConnection Connection { get; }
    public string Password { get; }
    public string GatewayPassword { get; }
    public int Width { get; }
    public int Height { get; }
    public double RenderScaling { get; }
    public RdpEffectiveQualitySettings Quality { get; }

    internal RdpConnectPlan CreateConnectPlan(RdpConnectEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var network = RdpSessionOptions.NormalizeNetworkSettings(Quality.ConnectionType);
        var desktopScalePercent = RdpSessionOptions.ClampDpiScalePercent(RenderScaling * 100);
        return new RdpConnectPlan
        {
            Host = Connection.Host,
            ConnectHost = RdpSessionOptions.ResolveDirectConnectHost(Connection.Host, environment),
            Port = RdpSessionOptions.NormalizePort(Connection.Port),
            Domain = Connection.Domain,
            Username = Connection.Username,
            Password = Password,
            GatewayHost = Connection.GatewayHost,
            GatewayDomain = Connection.GatewayDomain,
            GatewayUsername = Connection.GatewayUsername,
            GatewayPassword = GatewayPassword,
            Width = Width,
            Height = Height,
            ColorDepth = Quality.ColorDepth,
            ConnectionType = (uint)network.ConnectionType,
            NetworkAutoDetect = network.NetworkAutoDetect,
            UseNetworkLevelAuthentication = RdpSessionOptions.ShouldUseNetworkLevelAuthentication(Connection.Username, Password),
            KeyboardLayout = environment.GetKeyboardLayout(),
            DesktopScalePercent = desktopScalePercent,
            DeviceScalePercent = RdpSessionOptions.ToDeviceScalePercent(desktopScalePercent),
            Compression = Quality.Compression,
            BitmapCache = Quality.BitmapCache,
            FontSmoothing = Quality.FontSmoothing,
            DesktopWallpaper = Quality.DesktopWallpaper,
            Themes = Quality.Themes,
            MenuAnimations = Quality.MenuAnimations,
            FullWindowDrag = Quality.FullWindowDrag
        };
    }
}

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
        DpiScalePercent = (uint)Math.Clamp(Math.Round(RenderScaling * 100), RdpSessionOptions.MinDpiScalePercent, RdpSessionOptions.MaxDpiScalePercent);
        DeviceScalePercent = RdpSessionOptions.ToDeviceScalePercent(DpiScalePercent);
        Quality = quality with { ColorDepth = RdpSessionOptions.NormalizeColorDepth(quality.ColorDepth) };
    }

    public SavedConnection Connection { get; }
    public string Password { get; }
    public string GatewayPassword { get; }
    public int Width { get; }
    public int Height { get; }
    public double RenderScaling { get; }
    public uint DpiScalePercent { get; }
    public uint DeviceScalePercent { get; }
    public RdpEffectiveQualitySettings Quality { get; }
}

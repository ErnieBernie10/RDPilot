namespace RDPilot.Client.Models;

internal static class RdpSessionOptions
{
    public const int MinMonitorWidth = 200;
    public const int MaxMonitorWidth = 8192;
    public const int MinMonitorHeight = 200;
    public const int MaxMonitorHeight = 8192;

    public const int DefaultColorDepth = 16;
    public const RdpConnectionType DefaultConnectionType = RdpConnectionType.Wan;
    public const uint DefaultDpiScalePercent = 100;
    public const ushort DefaultPort = 3389;

    public static int NormalizeColorDepth(int colorDepth)
    {
        return colorDepth is 16 or 24 or 32 ? colorDepth : DefaultColorDepth;
    }

    public static ushort NormalizePort(ushort port)
    {
        return port == 0 ? DefaultPort : port;
    }

    public static int NormalizeConnectionType(int connectionType)
    {
        return connectionType >= (int)RdpConnectionType.Modem && connectionType <= (int)RdpConnectionType.Autodetect
            ? connectionType
            : (int)DefaultConnectionType;
    }

    public static int NormalizeConnectionType(RdpConnectionType connectionType)
    {
        return NormalizeConnectionType((int)connectionType);
    }

    public static (int ConnectionType, bool NetworkAutoDetect) NormalizeNetworkSettings(RdpConnectionType connectionType)
    {
        var normalized = NormalizeConnectionType(connectionType);
        return normalized == (int)RdpConnectionType.Autodetect
            ? (0, true)
            : (normalized, false);
    }

    // Windows RDP server only accepts three values for desktopScaleFactor/deviceScaleFactor:
    // 100 (96 DPI), 140 (134 DPI), 180 (173 DPI). Clamp the host's reported percentage to the
    // nearest valid step so the server honours it. Sending 200 or 125 silently falls back to 100.
    public static uint ClampDpiScalePercent(uint percent)
    {
        if (percent >= 150) return 180;
        if (percent >= 125) return 140;
        return 100;
    }

    public static (int Width, int Height) NormalizeResolution(int width, int height)
    {
        width = ClampInt(width, MinMonitorWidth, MaxMonitorWidth);
        height = ClampInt(height, MinMonitorHeight, MaxMonitorHeight);
        width -= width % 2;
        return (width, height);
    }

    private static int ClampInt(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}

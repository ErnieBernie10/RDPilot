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
        // FreeRDP also uses this flag to accept server-initiated RTT probes. Keep protocol
        // support enabled independently of whether the user selected a fixed quality hint.
        return (normalized == (int)RdpConnectionType.Autodetect ? 0 : normalized, true);
    }

    public static bool ShouldUseNetworkLevelAuthentication(string username, string password)
    {
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    public const uint MinDpiScalePercent = 100;
    public const uint MaxDpiScalePercent = 500;

    // desktopScaleFactor and deviceScaleFactor have different valid ranges (MS-RDPBCGR 2.2.1.3.2,
    // MS-RDPEDISP 2.2.2.2.1): the desktop factor is any percentage from 100 to 500, while the
    // device factor must be exactly 100, 140, or 180. The server ignores *both* if *either* is out
    // of range, so they are normalized separately and the real percentage is preserved for the
    // desktop factor - snapping 150% up to 180% there renders the remote ~20% too large.
    public static uint ClampDpiScalePercent(uint percent)
    {
        if (percent < MinDpiScalePercent) return MinDpiScalePercent;
        if (percent > MaxDpiScalePercent) return MaxDpiScalePercent;
        return percent;
    }

    // Snapped to the nearest legal step rather than rounded up. The midpoints are 120 and 160, so
    // the common Windows setting of 150% lands on 140 instead of overshooting to 180.
    public static uint ToDeviceScalePercent(uint percent)
    {
        if (percent < 120) return 100;
        if (percent < 160) return 140;
        return 180;
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

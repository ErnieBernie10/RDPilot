namespace RDPilot.Client.Models;

public static class RdpQualityDefaults
{
    public static RdpEffectiveQualitySettings Current { get; } = new(
        ColorDepth: 16,
        FontSmoothing: false,
        DesktopWallpaper: false,
        Themes: false,
        MenuAnimations: false,
        FullWindowDrag: false,
        Compression: true,
        BitmapCache: true,
        ConnectionType: RdpConnectionType.Wan);

    public static RdpEffectiveQualitySettings Resolve(RdpQualitySettings? global, RdpQualitySettings? overrides)
    {
        var defaults = Current;
        return new RdpEffectiveQualitySettings(
            NormalizeColorDepth(overrides?.ColorDepth ?? global?.ColorDepth ?? defaults.ColorDepth),
            overrides?.FontSmoothing ?? global?.FontSmoothing ?? defaults.FontSmoothing,
            overrides?.DesktopWallpaper ?? global?.DesktopWallpaper ?? defaults.DesktopWallpaper,
            overrides?.Themes ?? global?.Themes ?? defaults.Themes,
            overrides?.MenuAnimations ?? global?.MenuAnimations ?? defaults.MenuAnimations,
            overrides?.FullWindowDrag ?? global?.FullWindowDrag ?? defaults.FullWindowDrag,
            overrides?.Compression ?? global?.Compression ?? defaults.Compression,
            overrides?.BitmapCache ?? global?.BitmapCache ?? defaults.BitmapCache,
            overrides?.ConnectionType ?? global?.ConnectionType ?? defaults.ConnectionType);
    }

    private static int NormalizeColorDepth(int colorDepth)
    {
        return colorDepth is 16 or 24 or 32 ? colorDepth : Current.ColorDepth;
    }
}

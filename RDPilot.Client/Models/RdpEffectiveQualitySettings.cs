namespace RDPilot.Client.Models;

public readonly record struct RdpEffectiveQualitySettings(
    int ColorDepth,
    bool FontSmoothing,
    bool DesktopWallpaper,
    bool Themes,
    bool MenuAnimations,
    bool FullWindowDrag,
    bool Compression,
    bool BitmapCache,
    RdpConnectionType ConnectionType);

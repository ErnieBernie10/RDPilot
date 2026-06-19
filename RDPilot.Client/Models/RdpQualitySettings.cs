namespace RDPilot.Client.Models;

public sealed class RdpQualitySettings
{
    public int? ColorDepth { get; set; }
    public bool? FontSmoothing { get; set; }
    public bool? DesktopWallpaper { get; set; }
    public bool? Themes { get; set; }
    public bool? MenuAnimations { get; set; }
    public bool? FullWindowDrag { get; set; }
    public bool? Compression { get; set; }
    public bool? BitmapCache { get; set; }
    public RdpConnectionType? ConnectionType { get; set; }

    public RdpQualitySettings Clone()
    {
        return new RdpQualitySettings
        {
            ColorDepth = ColorDepth,
            FontSmoothing = FontSmoothing,
            DesktopWallpaper = DesktopWallpaper,
            Themes = Themes,
            MenuAnimations = MenuAnimations,
            FullWindowDrag = FullWindowDrag,
            Compression = Compression,
            BitmapCache = BitmapCache,
            ConnectionType = ConnectionType
        };
    }
}

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RDPilot.Client.Models;

namespace RDPilot.Client.ViewModels;

public partial class RdpQualitySettingsEditorViewModel : ViewModelBase
{
    private const string Inherit = "Inherit global";
    private const string Default = "Default";
    private const string Enabled = "Enabled";
    private const string Disabled = "Disabled";
    private readonly bool _allowInherit;

    [ObservableProperty] private string _colorDepthSelection;
    [ObservableProperty] private string _fontSmoothingSelection;
    [ObservableProperty] private string _desktopWallpaperSelection;
    [ObservableProperty] private string _themesSelection;
    [ObservableProperty] private string _menuAnimationsSelection;
    [ObservableProperty] private string _fullWindowDragSelection;
    [ObservableProperty] private string _compressionSelection;
    [ObservableProperty] private string _bitmapCacheSelection;
    [ObservableProperty] private string _connectionTypeSelection;

    public RdpQualitySettingsEditorViewModel(RdpQualitySettings? settings, bool allowInherit, RdpQualitySettings? inheritedSettings = null)
    {
        _allowInherit = allowInherit;
        var inherited = allowInherit
            ? RdpQualityDefaults.Resolve(inheritedSettings, null)
            : RdpQualityDefaults.Current;
        var colorDepthUnset = UnsetOption(FormatColorDepth(inherited.ColorDepth));
        var fontSmoothingUnset = UnsetOption(FormatBool(inherited.FontSmoothing));
        var desktopWallpaperUnset = UnsetOption(FormatBool(inherited.DesktopWallpaper));
        var themesUnset = UnsetOption(FormatBool(inherited.Themes));
        var menuAnimationsUnset = UnsetOption(FormatBool(inherited.MenuAnimations));
        var fullWindowDragUnset = UnsetOption(FormatBool(inherited.FullWindowDrag));
        var compressionUnset = UnsetOption(FormatBool(inherited.Compression));
        var bitmapCacheUnset = UnsetOption(FormatBool(inherited.BitmapCache));
        var connectionTypeUnset = UnsetOption(FormatConnectionType(inherited.ConnectionType));

        ColorDepthOptions = new[] { colorDepthUnset, "16-bit", "24-bit", "32-bit" };
        FontSmoothingOptions = BooleanOptions(fontSmoothingUnset);
        DesktopWallpaperOptions = BooleanOptions(desktopWallpaperUnset);
        ThemesOptions = BooleanOptions(themesUnset);
        MenuAnimationsOptions = BooleanOptions(menuAnimationsUnset);
        FullWindowDragOptions = BooleanOptions(fullWindowDragUnset);
        CompressionOptions = BooleanOptions(compressionUnset);
        BitmapCacheOptions = BooleanOptions(bitmapCacheUnset);
        ConnectionTypeOptions = new[]
        {
            connectionTypeUnset,
            "Modem",
            "Broadband low",
            "Satellite",
            "Broadband high",
            "WAN",
            "LAN",
            "Autodetect"
        };

        _colorDepthSelection = settings?.ColorDepth switch
        {
            16 => "16-bit",
            24 => "24-bit",
            32 => "32-bit",
            _ => colorDepthUnset
        };
        _fontSmoothingSelection = FromBool(settings?.FontSmoothing, fontSmoothingUnset);
        _desktopWallpaperSelection = FromBool(settings?.DesktopWallpaper, desktopWallpaperUnset);
        _themesSelection = FromBool(settings?.Themes, themesUnset);
        _menuAnimationsSelection = FromBool(settings?.MenuAnimations, menuAnimationsUnset);
        _fullWindowDragSelection = FromBool(settings?.FullWindowDrag, fullWindowDragUnset);
        _compressionSelection = FromBool(settings?.Compression, compressionUnset);
        _bitmapCacheSelection = FromBool(settings?.BitmapCache, bitmapCacheUnset);
        _connectionTypeSelection = FromConnectionType(settings?.ConnectionType, connectionTypeUnset);
    }

    public string[] ColorDepthOptions { get; }
    public string[] FontSmoothingOptions { get; }
    public string[] DesktopWallpaperOptions { get; }
    public string[] ThemesOptions { get; }
    public string[] MenuAnimationsOptions { get; }
    public string[] FullWindowDragOptions { get; }
    public string[] CompressionOptions { get; }
    public string[] BitmapCacheOptions { get; }
    public string[] ConnectionTypeOptions { get; }
    public string UnsetLabel => _allowInherit ? Inherit : Default;

    public RdpQualitySettings BuildSettings()
    {
        return new RdpQualitySettings
        {
            ColorDepth = ColorDepthSelection switch
            {
                "16-bit" => 16,
                "24-bit" => 24,
                "32-bit" => 32,
                _ => null
            },
            FontSmoothing = ToBool(FontSmoothingSelection),
            DesktopWallpaper = ToBool(DesktopWallpaperSelection),
            Themes = ToBool(ThemesSelection),
            MenuAnimations = ToBool(MenuAnimationsSelection),
            FullWindowDrag = ToBool(FullWindowDragSelection),
            Compression = ToBool(CompressionSelection),
            BitmapCache = ToBool(BitmapCacheSelection),
            ConnectionType = ToConnectionType(ConnectionTypeSelection)
        };
    }

    public bool HasAnyValue()
    {
        var settings = BuildSettings();
        return settings.ColorDepth.HasValue
            || settings.FontSmoothing.HasValue
            || settings.DesktopWallpaper.HasValue
            || settings.Themes.HasValue
            || settings.MenuAnimations.HasValue
            || settings.FullWindowDrag.HasValue
            || settings.Compression.HasValue
            || settings.BitmapCache.HasValue
            || settings.ConnectionType.HasValue;
    }

    private static string FromBool(bool? value, string unset)
    {
        return value switch
        {
            true => Enabled,
            false => Disabled,
            _ => unset
        };
    }

    private string UnsetOption(string value)
    {
        return $"{UnsetLabel} ({value})";
    }

    private static string[] BooleanOptions(string unset)
    {
        return new[] { unset, Enabled, Disabled };
    }

    private static string FormatColorDepth(int colorDepth)
    {
        return $"{colorDepth}-bit";
    }

    private static string FormatBool(bool value)
    {
        return value ? "enabled" : "disabled";
    }

    private static string FormatConnectionType(RdpConnectionType value)
    {
        return FromConnectionType(value, Default);
    }

    private static bool? ToBool(string value)
    {
        return value switch
        {
            Enabled => true,
            Disabled => false,
            _ => null
        };
    }

    private static string FromConnectionType(RdpConnectionType? value, string unset)
    {
        return value switch
        {
            RdpConnectionType.Modem => "Modem",
            RdpConnectionType.BroadbandLow => "Broadband low",
            RdpConnectionType.Satellite => "Satellite",
            RdpConnectionType.BroadbandHigh => "Broadband high",
            RdpConnectionType.Wan => "WAN",
            RdpConnectionType.Lan => "LAN",
            RdpConnectionType.Autodetect => "Autodetect",
            _ => unset
        };
    }

    private static RdpConnectionType? ToConnectionType(string value)
    {
        return value switch
        {
            "Modem" => RdpConnectionType.Modem,
            "Broadband low" => RdpConnectionType.BroadbandLow,
            "Satellite" => RdpConnectionType.Satellite,
            "Broadband high" => RdpConnectionType.BroadbandHigh,
            "WAN" => RdpConnectionType.Wan,
            "LAN" => RdpConnectionType.Lan,
            "Autodetect" => RdpConnectionType.Autodetect,
            _ => null
        };
    }
}

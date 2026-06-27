using Avalonia;
using System;

namespace RDPilot.Client;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (ShouldUseWayland())
        {
            builder.UseWayland();
        }

        return builder
            .WithInterFont()
            .LogToTrace();
    }

    private static bool ShouldUseWayland()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var overrideValue = Environment.GetEnvironmentVariable("RDPILOT_USE_WAYLAND");
        if (string.Equals(overrideValue, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(overrideValue, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(overrideValue, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(overrideValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RDPilot.Client.Services;

public static class AppDataPaths
{
    public const string AppName = "RDPilot.Client";

    public static string ConfigDirectory
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support",
                    AppName);
            }

            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            {
                return Path.Combine(xdgConfigHome, AppName);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                AppName);
        }
    }

    public static string ConnectionsFilePath => Path.Combine(ConfigDirectory, "connections.json");
    public static string CertificateTrustFilePath => Path.Combine(ConfigDirectory, "trusted-certificates.json");
}

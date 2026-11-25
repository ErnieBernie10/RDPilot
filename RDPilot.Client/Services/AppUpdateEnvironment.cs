using System.IO;
using System.Net.Http;
using System.Reflection;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed record AppUpdateEnvironment(AppUpdateChannel Channel, Version? CurrentVersion, IUpdateFeed Feed, IUpdateInstaller Installer)
{
    private static readonly Version DevelopmentVersion = new(0, 1, 0);

    public static AppUpdateEnvironment Disabled { get; } = new(AppUpdateChannel.None, null, Unavailable.Instance, Unavailable.Instance);

    public static AppUpdateEnvironment Detect()
    {
        var informational = typeof(AppUpdateEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Version? version = TryParseVersion(informational, out var parsed) ? parsed : null;
        var channel = DetectChannel(version, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), AppContext.BaseDirectory, File.Exists);
        if (channel == AppUpdateChannel.None)
        {
            return Disabled with { CurrentVersion = version };
        }

        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RDPilot/{version}");
        return new AppUpdateEnvironment(channel, version, new GitHubReleaseFeed(http), new InnoUpdateInstaller(http));
    }

    internal static AppUpdateChannel DetectChannel(Version? version, bool isWindows, bool isLinux, string baseDirectory, Func<string, bool> fileExists)
    {
        if (version == null || version == DevelopmentVersion)
        {
            return AppUpdateChannel.None;
        }

        if (isWindows)
        {
            return fileExists(Path.Combine(baseDirectory, "unins000.exe")) ? AppUpdateChannel.Installer : AppUpdateChannel.None;
        }

        if (isLinux)
        {
            return fileExists("/.flatpak-info") ? AppUpdateChannel.None : AppUpdateChannel.PackageManager;
        }

        return AppUpdateChannel.None;
    }

    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = DevelopmentVersion;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private sealed class Unavailable : IUpdateFeed, IUpdateInstaller
    {
        public static Unavailable Instance { get; } = new();

        public Task<AvailableUpdate?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<AvailableUpdate?>(null);

        public Task<string> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Updates are not available for this installation.");

        public void Launch(string installerPath, bool relaunch) =>
            throw new InvalidOperationException("Updates are not available for this installation.");
    }
}

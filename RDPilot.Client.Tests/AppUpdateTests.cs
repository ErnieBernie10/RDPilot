using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

public class AppUpdateTests
{
    private const string Checksum = "3F2A0E1B9C8D7E6F5A4B3C2D1E0F9A8B7C6D5E4F3A2B1C0D9E8F7A6B5C4D3E2F";

    [Fact]
    public void Parse_FindsInstallerAndChecksumForTheTaggedVersion()
    {
        var update = GitHubReleaseFeed.Parse("""
            {
              "tag_name": "v1.6.0",
              "html_url": "https://github.com/ErnieBernie10/RDPilot/releases/tag/v1.6.0",
              "assets": [
                { "name": "rdpilot-1.6.0-1-x86_64.pkg.tar.zst", "browser_download_url": "https://example.test/arch" },
                { "name": "RDPilot-Setup-1.6.0-win-x64.exe", "browser_download_url": "https://example.test/setup.exe" },
                { "name": "RDPilot-Setup-1.6.0-win-x64.exe.sha256", "browser_download_url": "https://example.test/setup.exe.sha256" }
              ]
            }
            """);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 6, 0), update.Version);
        Assert.Equal("https://github.com/ErnieBernie10/RDPilot/releases/tag/v1.6.0", update.ReleasePageUrl);
        Assert.Equal("https://example.test/setup.exe", update.InstallerUrl);
        Assert.Equal("https://example.test/setup.exe.sha256", update.ChecksumUrl);
    }

    [Fact]
    public void Parse_ReturnsNullForAnUnversionedTag()
    {
        Assert.Null(GitHubReleaseFeed.Parse("""{ "tag_name": "nightly", "assets": [] }"""));
    }

    [Theory]
    [InlineData(Checksum + "\r\n")]
    [InlineData(Checksum + "  RDPilot-Setup-1.6.0-win-x64.exe\n")]
    public void ParseChecksum_ReadsTheLeadingHash(string text)
    {
        Assert.Equal(Checksum, InnoUpdateInstaller.ParseChecksum(text));
    }

    [Fact]
    public void ParseChecksum_RejectsGarbage()
    {
        Assert.Null(InnoUpdateInstaller.ParseChecksum("<html>Not Found</html>"));
    }

    [Theory]
    [InlineData("1.5.0", true, false, true, AppUpdateChannel.Installer)]
    [InlineData("1.5.0", true, false, false, AppUpdateChannel.None)]
    [InlineData("0.1.0", true, false, true, AppUpdateChannel.None)]
    [InlineData("1.5.0", false, true, false, AppUpdateChannel.PackageManager)]
    [InlineData("1.5.0", false, true, true, AppUpdateChannel.None)]
    public void DetectChannel_MatchesTheInstallation(string version, bool isWindows, bool isLinux, bool markerExists, AppUpdateChannel expected)
    {
        var marker = isWindows ? Path.Combine("app", "unins000.exe") : "/.flatpak-info";
        var channel = AppUpdateEnvironment.DetectChannel(Version.Parse(version), isWindows, isLinux, "app", path => markerExists && path == marker);
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData("1.5.0+3f2a0e1", 1, 5, 0)]
    [InlineData("v2.0", 2, 0, 0)]
    public void TryParseVersion_NormalizesToThreeParts(string text, int major, int minor, int build)
    {
        Assert.True(AppUpdateEnvironment.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public async Task CheckNow_ShowsANewerRelease()
    {
        var updates = CreateUpdates(AppUpdateChannel.Installer, latest: "1.6.0");

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.Available, updates.State);
        Assert.True(updates.IsNoticeVisible);
        Assert.Equal("Update to 1.6.0", updates.NoticeText);
        Assert.Equal("RDPilot 1.6.0 is available.", updates.CheckResult);
    }

    [Fact]
    public async Task CheckNow_ReportsUpToDate()
    {
        var updates = CreateUpdates(AppUpdateChannel.Installer, latest: "1.5.0");

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.Idle, updates.State);
        Assert.False(updates.IsNoticeVisible);
        Assert.Equal("RDPilot is up to date.", updates.CheckResult);
    }

    [Fact]
    public async Task CheckNow_ReportsFailures()
    {
        var feed = new FakeFeed { Error = new HttpRequestException("offline") };
        var updates = new AppUpdateViewModel(new AppUpdateEnvironment(AppUpdateChannel.Installer, new Version(1, 5, 0), feed, new FakeInstaller()), () => 0);

        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("Could not check for updates: offline", updates.CheckResult);
        Assert.False(updates.IsNoticeVisible);
    }

    [Fact]
    public async Task Update_WithNoOpenSessions_InstallsAndRelaunchesImmediately()
    {
        var installer = new FakeInstaller();
        var updates = CreateUpdates(AppUpdateChannel.Installer, latest: "1.6.0", installer: installer);
        var shutdowns = 0;
        updates.ShutdownRequested += (_, _) => shutdowns++;

        await updates.CheckNowCommand.ExecuteAsync(null);
        await updates.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal([("setup.exe", true)], installer.Launches);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task Update_WithOpenSessions_WaitsForTheUserAndCanInstallOnExit()
    {
        var installer = new FakeInstaller();
        var updates = CreateUpdates(AppUpdateChannel.Installer, latest: "1.6.0", installer: installer, openSessions: 2);
        var shutdowns = 0;
        updates.ShutdownRequested += (_, _) => shutdowns++;

        await updates.CheckNowCommand.ExecuteAsync(null);
        await updates.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.ReadyToInstall, updates.State);
        Assert.Equal("2 sessions will disconnect. Update now?", updates.PanelDetail);
        Assert.Empty(installer.Launches);

        updates.InstallOnExitCommand.Execute(null);
        updates.InstallPendingOnExit();

        Assert.Equal([("setup.exe", false)], installer.Launches);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task Update_DownloadFailure_ShowsTheReasonAndKeepsRunning()
    {
        var installer = new FakeInstaller { DownloadError = new InvalidDataException("checksum mismatch") };
        var updates = CreateUpdates(AppUpdateChannel.Installer, latest: "1.6.0", installer: installer);

        await updates.CheckNowCommand.ExecuteAsync(null);
        await updates.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.Failed, updates.State);
        Assert.True(updates.IsNoticeVisible);
        Assert.Equal("checksum mismatch", updates.PanelDetail);
        Assert.Empty(installer.Launches);
    }

    [Fact]
    public async Task PackageManagerInstalls_OnlyNotify()
    {
        var installer = new FakeInstaller();
        var updates = CreateUpdates(AppUpdateChannel.PackageManager, latest: "1.6.0", installer: installer);

        await updates.CheckNowCommand.ExecuteAsync(null);
        await updates.StartUpdateCommand.ExecuteAsync(null);

        Assert.Equal(AppUpdateState.Available, updates.State);
        Assert.False(updates.CanStartUpdate);
        Assert.Contains("package manager", updates.PanelDetail);
        Assert.Equal(0, installer.Downloads);
    }

    [Fact]
    public async Task Later_HidesTheNoticeUntilANewerReleaseAppears()
    {
        var feed = new FakeFeed { Latest = Release("1.6.0") };
        var updates = new AppUpdateViewModel(new AppUpdateEnvironment(AppUpdateChannel.Installer, new Version(1, 5, 0), feed, new FakeInstaller()), () => 0);

        await updates.CheckNowCommand.ExecuteAsync(null);
        updates.LaterCommand.Execute(null);
        Assert.False(updates.IsNoticeVisible);

        feed.Latest = Release("1.7.0");
        await updates.CheckNowCommand.ExecuteAsync(null);

        Assert.True(updates.IsNoticeVisible);
        Assert.Equal("Update to 1.7.0", updates.NoticeText);
    }

    private static AppUpdateViewModel CreateUpdates(AppUpdateChannel channel, string latest, FakeInstaller? installer = null, int openSessions = 0)
    {
        var environment = new AppUpdateEnvironment(channel, new Version(1, 5, 0), new FakeFeed { Latest = Release(latest) }, installer ?? new FakeInstaller());
        return new AppUpdateViewModel(environment, () => openSessions);
    }

    private static AvailableUpdate Release(string version) =>
        new(Version.Parse(version), $"https://example.test/releases/{version}", "https://example.test/setup.exe", "https://example.test/setup.exe.sha256");

    private sealed class FakeFeed : IUpdateFeed
    {
        public AvailableUpdate? Latest { get; set; }
        public Exception? Error { get; init; }

        public Task<AvailableUpdate?> GetLatestAsync(CancellationToken cancellationToken) =>
            Error != null ? Task.FromException<AvailableUpdate?>(Error) : Task.FromResult(Latest);
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public Exception? DownloadError { get; init; }
        public int Downloads { get; private set; }
        public List<(string Path, bool Relaunch)> Launches { get; } = [];

        public Task<string> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return DownloadError != null ? Task.FromException<string>(DownloadError) : Task.FromResult("setup.exe");
        }

        public void Launch(string installerPath, bool relaunch) => Launches.Add((installerPath, relaunch));
    }
}

namespace RDPilot.Client.Models;

public sealed record AvailableUpdate(Version Version, string ReleasePageUrl, string? InstallerUrl, string? ChecksumUrl);

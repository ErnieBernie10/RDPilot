using System.Net.Http;
using System.Text.Json;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed class GitHubReleaseFeed : IUpdateFeed
{
    public const string ReleasesPageUrl = "https://github.com/ErnieBernie10/RDPilot/releases";
    private const string LatestReleaseUrl = "https://api.github.com/repos/ErnieBernie10/RDPilot/releases/latest";

    private readonly HttpClient _http;

    public GitHubReleaseFeed(HttpClient http)
    {
        _http = http;
    }

    public async Task<AvailableUpdate?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static AvailableUpdate? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) || !AppUpdateEnvironment.TryParseVersion(tag.GetString(), out var version))
        {
            return null;
        }

        var releasePage = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
        var installerName = $"RDPilot-Setup-{version.ToString(3)}-win-x64.exe";
        string? installerUrl = null;
        string? checksumUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = url;
                }
                else if (string.Equals(name, installerName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        return new AvailableUpdate(version, releasePage ?? ReleasesPageUrl, installerUrl, checksumUrl);
    }
}

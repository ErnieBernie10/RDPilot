using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed class InnoUpdateInstaller : IUpdateInstaller
{
    private const char ByteOrderMark = (char)0xFEFF;
    private readonly HttpClient _http;

    public InnoUpdateInstaller(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (update.InstallerUrl == null || update.ChecksumUrl == null)
        {
            throw new InvalidOperationException("This release has no Windows installer.");
        }

        var expected = ParseChecksum(await _http.GetStringAsync(update.ChecksumUrl, cancellationToken))
            ?? throw new InvalidDataException("The published checksum is unreadable.");

        var directory = Path.Combine(Path.GetTempPath(), "RDPilot-Update");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(new Uri(update.InstallerUrl).LocalPath));

        using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0)
                {
                    progress.Report((double)received / total.Value);
                }
            }
        }

        if (!string.Equals(expected, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded installer does not match the published checksum.");
        }

        return path;
    }

    public void Launch(string installerPath, bool relaunch)
    {
        var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        if (relaunch)
        {
            startInfo.ArgumentList.Add("/relaunch=1");
        }

        Process.Start(startInfo)?.Dispose();
    }

    internal static string? ParseChecksum(string text)
    {
        var token = text.Trim().TrimStart(ByteOrderMark).Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : null;
    }
}

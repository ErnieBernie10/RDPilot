using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RDPilot.Client.Services;

/// <summary>Materializes remote clipboard files on disk for platforms that cannot paste virtual files.</summary>
internal static class RemoteFileDownloader
{
    private static readonly char[] SeparatorChars = ['\\', '/'];

    public static async Task<string[]> DownloadAsync(RemoteFileOffer offer, string cacheDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        DeletePreviousDownloads(cacheDirectory);
        var root = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var topLevel = new List<string>();
            var buffer = new byte[RemoteFileReader.ChunkSize];
            foreach (var entry in offer.Files.Entries)
            {
                var segments = ToSafeSegments(entry.RelativePath);
                var path = Path.Combine(root, Path.Combine(segments));
                var top = Path.Combine(root, segments[0]);
                if (!topLevel.Contains(top)) topLevel.Add(top);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var reader = offer.OpenReader(entry))
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    int read;
                    while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (entry.LastWriteTime is { } lastWrite) File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            }

            return topLevel.ToArray();
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    internal static string[] ToSafeSegments(string relativePath)
    {
        var segments = relativePath.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new IOException($"Invalid remote file name '{relativePath}'.");
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i] is "." or "..") throw new IOException($"Invalid remote file name '{relativePath}'.");
            var chars = segments[i].ToCharArray();
            for (var c = 0; c < chars.Length; c++)
            {
                if (chars[c] < ' ' || chars[c] is ':' or '*' or '?' or '"' or '<' or '>' or '|') chars[c] = '_';
            }
            segments[i] = new string(chars);
        }
        return segments;
    }

    private static void DeletePreviousDownloads(string cacheDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(cacheDirectory)) TryDelete(directory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Unable to delete clipboard download {0}: {1}", directory, ex.Message);
        }
    }
}

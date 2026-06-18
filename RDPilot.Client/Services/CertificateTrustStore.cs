using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RDPilot.Client.Services;

public sealed class CertificateTrustStore : ICertificateTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _lock = new();

    public string? GetTrustedFingerprint(string host, ushort port)
    {
        lock (_lock)
        {
            var entries = LoadUnsafe();
            return entries.TryGetValue(Key(host, port), out var entry) ? entry.Fingerprint : null;
        }
    }

    public void SaveTrustedFingerprint(string host, ushort port, string fingerprint)
    {
        lock (_lock)
        {
            var entries = LoadUnsafe();
            entries[Key(host, port)] = new CertificateTrustEntry
            {
                Host = host,
                Port = port,
                Fingerprint = fingerprint,
                TrustedAt = DateTimeOffset.UtcNow
            };
            SaveUnsafe(entries);
        }
    }

    private static string Key(string host, ushort port) => $"{host}:{port}";

    private static Dictionary<string, CertificateTrustEntry> LoadUnsafe()
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);
        if (!File.Exists(AppDataPaths.CertificateTrustFilePath))
        {
            return new Dictionary<string, CertificateTrustEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(AppDataPaths.CertificateTrustFilePath);
        return JsonSerializer.Deserialize<Dictionary<string, CertificateTrustEntry>>(json, JsonOptions)
            ?? new Dictionary<string, CertificateTrustEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveUnsafe(Dictionary<string, CertificateTrustEntry> entries)
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);
        var tempPath = AppDataPaths.CertificateTrustFilePath + ".tmp";
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, AppDataPaths.CertificateTrustFilePath, true);
    }

    private sealed class CertificateTrustEntry
    {
        public string Host { get; set; } = "";
        public ushort Port { get; set; }
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset TrustedAt { get; set; }
    }
}

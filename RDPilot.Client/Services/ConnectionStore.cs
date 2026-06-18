using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISecretStore _secretStore;

    public ConnectionStore(ISecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public string ConnectionsFilePath => AppDataPaths.ConnectionsFilePath;
    public string SecretStoreDescription => _secretStore.Description;

    public async Task<IReadOnlyList<SavedConnection>> LoadAsync()
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);

        if (!File.Exists(AppDataPaths.ConnectionsFilePath))
        {
            var imported = await TryImportLocalConnectionAsync();
            if (imported.Count > 0)
            {
                await SaveAllAsync(imported);
            }

            return imported;
        }

        var json = await File.ReadAllTextAsync(AppDataPaths.ConnectionsFilePath);
        var connections = JsonSerializer.Deserialize<List<SavedConnection>>(json, JsonOptions) ?? new List<SavedConnection>();
        return connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task SaveAsync(SavedConnection connection, string? password, bool passwordChanged, string? gatewayPassword, bool gatewayPasswordChanged)
    {
        var connections = (await LoadAsync()).Select(c => c.Clone()).ToList();
        var existing = connections.FindIndex(c => c.Id == connection.Id);
        var now = DateTimeOffset.UtcNow;

        connection.UpdatedAt = now;
        if (connection.CreatedAt == default)
        {
            connection.CreatedAt = now;
        }

        if (existing >= 0)
        {
            connection.CreatedAt = connections[existing].CreatedAt;
            connections[existing] = connection.Clone();
        }
        else
        {
            connection.CreatedAt = now;
            connections.Add(connection.Clone());
        }

        if (passwordChanged)
        {
            await SaveOrDeleteSecretAsync(SecretStore.PasswordKey(connection.Id), password);
        }

        if (gatewayPasswordChanged)
        {
            await SaveOrDeleteSecretAsync(SecretStore.GatewayPasswordKey(connection.Id), gatewayPassword);
        }

        await SaveAllAsync(connections);
    }

    public async Task DeleteAsync(SavedConnection connection)
    {
        var connections = (await LoadAsync()).Where(c => c.Id != connection.Id).ToList();
        await SaveAllAsync(connections);
        await _secretStore.DeleteSecretAsync(SecretStore.PasswordKey(connection.Id));
        await _secretStore.DeleteSecretAsync(SecretStore.GatewayPasswordKey(connection.Id));
    }

    public Task<string?> GetPasswordAsync(SavedConnection connection)
    {
        return _secretStore.GetSecretAsync(SecretStore.PasswordKey(connection.Id));
    }

    public Task<string?> GetGatewayPasswordAsync(SavedConnection connection)
    {
        return _secretStore.GetSecretAsync(SecretStore.GatewayPasswordKey(connection.Id));
    }

    private static async Task SaveAllAsync(IEnumerable<SavedConnection> connections)
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);
        var ordered = connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var tempPath = AppDataPaths.ConnectionsFilePath + ".tmp";
        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, AppDataPaths.ConnectionsFilePath, true);
    }

    private async Task SaveOrDeleteSecretAsync(string key, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            await _secretStore.DeleteSecretAsync(key);
            return;
        }

        await _secretStore.SetSecretAsync(key, secret);
    }

    private async Task<List<SavedConnection>> TryImportLocalConnectionAsync()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "connection.local.json");
        if (!File.Exists(settingsPath))
        {
            return new List<SavedConnection>();
        }

        var settings = JsonSerializer.Deserialize<LocalConnectionSettings>(await File.ReadAllTextAsync(settingsPath), JsonOptions);
        if (settings == null || string.IsNullOrWhiteSpace(settings.Host))
        {
            return new List<SavedConnection>();
        }

        var connection = new SavedConnection
        {
            Name = string.IsNullOrWhiteSpace(settings.Name) ? settings.Host : settings.Name,
            Host = settings.Host ?? "",
            Domain = settings.Domain ?? "",
            Username = settings.Username ?? "",
            GatewayHost = settings.GatewayHost ?? "",
            GatewayDomain = settings.GatewayDomain ?? "",
            GatewayUsername = settings.GatewayUsername ?? ""
        };

        if (!string.IsNullOrEmpty(settings.Password))
        {
            await _secretStore.SetSecretAsync(SecretStore.PasswordKey(connection.Id), settings.Password);
        }

        if (!string.IsNullOrEmpty(settings.GatewayPassword))
        {
            await _secretStore.SetSecretAsync(SecretStore.GatewayPasswordKey(connection.Id), settings.GatewayPassword);
        }

        return new List<SavedConnection> { connection };
    }

    private sealed class LocalConnectionSettings
    {
        public string? Name { get; set; }
        public string? Host { get; set; }
        public string? Domain { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? GatewayHost { get; set; }
        public string? GatewayDomain { get; set; }
        public string? GatewayUsername { get; set; }
        public string? GatewayPassword { get; set; }
    }
}

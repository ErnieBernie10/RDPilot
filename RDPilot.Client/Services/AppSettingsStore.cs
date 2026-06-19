using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string SettingsFilePath => AppDataPaths.SettingsFilePath;

    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);

        if (!File.Exists(AppDataPaths.SettingsFilePath))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(AppDataPaths.SettingsFilePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.QualitySettings ??= new RdpQualitySettings();
        return settings;
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataPaths.ConfigDirectory);
        var tempPath = AppDataPaths.SettingsFilePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, AppDataPaths.SettingsFilePath, true);
    }
}

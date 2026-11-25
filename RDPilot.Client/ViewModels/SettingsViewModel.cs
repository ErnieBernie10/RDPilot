using RDPilot.Client.Models;

namespace RDPilot.Client.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(AppSettings settings, AppUpdateViewModel updates)
    {
        QualityEditor = new RdpQualitySettingsEditorViewModel(settings.QualitySettings, allowInherit: false);
        CheckForUpdatesAutomatically = settings.CheckForUpdatesAutomatically;
        Updates = updates;
    }

    public RdpQualitySettingsEditorViewModel QualityEditor { get; }
    public AppUpdateViewModel Updates { get; }
    public bool CheckForUpdatesAutomatically { get; set; }

    public AppSettings BuildSettings()
    {
        return new AppSettings
        {
            QualitySettings = QualityEditor.BuildSettings(),
            CheckForUpdatesAutomatically = CheckForUpdatesAutomatically
        };
    }
}

using RDPilot.Client.Models;

namespace RDPilot.Client.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(AppSettings settings)
    {
        QualityEditor = new RdpQualitySettingsEditorViewModel(settings.QualitySettings, allowInherit: false);
    }

    public RdpQualitySettingsEditorViewModel QualityEditor { get; }

    public AppSettings BuildSettings()
    {
        return new AppSettings
        {
            QualitySettings = QualityEditor.BuildSettings()
        };
    }
}

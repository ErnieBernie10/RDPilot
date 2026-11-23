namespace RDPilot.Client.Models;

public sealed class AppSettings
{
    public RdpQualitySettings QualitySettings { get; set; } = new();
    public bool CheckForUpdatesAutomatically { get; set; } = true;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            QualitySettings = QualitySettings.Clone(),
            CheckForUpdatesAutomatically = CheckForUpdatesAutomatically
        };
    }
}

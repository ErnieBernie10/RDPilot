namespace RDPilot.Client.Models;

public sealed class AppSettings
{
    public RdpQualitySettings QualitySettings { get; set; } = new();

    public AppSettings Clone()
    {
        return new AppSettings
        {
            QualitySettings = QualitySettings.Clone()
        };
    }
}

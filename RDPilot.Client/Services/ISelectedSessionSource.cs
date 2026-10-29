using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Services;

public interface ISelectedSessionSource
{
    RdpSessionViewModel? SelectedSession { get; }

    event EventHandler? SelectedSessionChanged;
}

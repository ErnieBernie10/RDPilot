namespace RDPilot.Client.Models;

public enum AppUpdateState
{
    Idle,
    Available,
    Downloading,
    ReadyToInstall,
    InstallOnExit,
    Failed
}

using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public interface IUpdateInstaller
{
    Task<string> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken);
    void Launch(string installerPath, bool relaunch);
}

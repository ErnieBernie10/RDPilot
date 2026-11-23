using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public interface IUpdateFeed
{
    Task<AvailableUpdate?> GetLatestAsync(CancellationToken cancellationToken);
}

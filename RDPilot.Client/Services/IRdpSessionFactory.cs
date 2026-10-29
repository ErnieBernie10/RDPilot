using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Services;

public interface IRdpSessionFactory
{
    RdpSessionViewModel Create(RdpSessionStartRequest request);
}

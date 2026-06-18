using System;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Services;

public interface IRdpSessionFactory
{
    RdpSessionViewModel Create(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived);
}

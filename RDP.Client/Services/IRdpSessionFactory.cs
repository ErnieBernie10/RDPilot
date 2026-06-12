using System;
using RDP.Client.Models;
using RDP.Client.ViewModels;

namespace RDP.Client.Services;

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

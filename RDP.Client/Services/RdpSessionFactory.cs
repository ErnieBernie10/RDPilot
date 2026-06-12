using System;
using RDP.Client.Models;
using RDP.Client.ViewModels;

namespace RDP.Client.Services;

public sealed class RdpSessionFactory : IRdpSessionFactory
{
    public RdpSessionViewModel Create(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        int width,
        int height,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
    {
        return new RdpSessionViewModel(
            connection,
            password,
            gatewayPassword,
            width,
            height,
            remoteClipboardTextReceived);
    }
}

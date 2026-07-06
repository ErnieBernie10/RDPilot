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
        double renderScaling,
        int colorDepth,
        bool compression,
        bool fontSmoothing,
        bool bitmapCache,
        bool desktopWallpaper,
        bool themes,
        bool menuAnimations,
        bool fullWindowDrag,
        RdpConnectionType connectionType,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived);
}

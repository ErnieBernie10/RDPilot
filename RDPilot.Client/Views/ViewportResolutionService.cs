using System.Diagnostics.CodeAnalysis;
using Avalonia;

namespace RDPilot.Client.Views;

[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance service for presenter composition consistency.")]
internal sealed class ViewportResolutionService
{
    private const int MinimumRemoteWidth = 640;
    private const int MinimumRemoteHeight = 480;

    public bool TryCompute(Size viewportSize, double renderScaling, bool isMinimized, out int width, out int height, out double normalizedScaling)
    {
        normalizedScaling = renderScaling > 0 ? renderScaling : 1.0;
        width = 0;
        height = 0;

        if (isMinimized)
        {
            return false;
        }

        width = (int)(viewportSize.Width * normalizedScaling);
        height = (int)(viewportSize.Height * normalizedScaling);
        return width >= MinimumRemoteWidth && height >= MinimumRemoteHeight;
    }
}

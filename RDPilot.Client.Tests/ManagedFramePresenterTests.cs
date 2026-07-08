using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ManagedFramePresenterTests
{
    [Fact]
    public void EnqueueFrame_RecreatesBitmapAndRetriesWhenFramebufferSizeChanges()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        (int Width, int Height)? firstScreen = null;
        (int Width, int Height)? secondScreen = null;
        var redrawCount = 0;
        var presentCallCount = 0;

        using var presenter = new ManagedFramePresenter(
            "Session",
            width: 64,
            height: 32,
            setScreen: screen =>
            {
                if (screen is null)
                {
                    return;
                }

                var size = (screen.PixelSize.Width, screen.PixelSize.Height);
                if (firstScreen is null)
                {
                    firstScreen = size;
                }
                else
                {
                    secondScreen = size;
                }
            },
            requestRedraw: () => redrawCount++,
            present: (IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight) =>
            {
                presentCallCount++;
                dirtyX = 0;
                dirtyY = 0;
                dirtyWidth = 80;
                dirtyHeight = 40;
                fbWidth = 80;
                fbHeight = 40;

                return presentCallCount > 1;
            });

        presenter.EnqueueFrame(80, 40);
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Equal((64, 32), firstScreen);
        Assert.Equal((80, 40), secondScreen);
        Assert.Equal(2, presentCallCount);
        Assert.Equal(1, redrawCount);
    }

    [Fact]
    public void Dispose_BeforeQueuedPresent_DropsPendingFrameWork()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var setScreenCount = 0;
        var redrawCount = 0;
        var presentCallCount = 0;

        using var presenter = new ManagedFramePresenter(
            "Session",
            width: 1,
            height: 1,
            setScreen: _ => setScreenCount++,
            requestRedraw: () => redrawCount++,
            present: (IntPtr dest, int destStride, int destWidth, int destHeight, out int dirtyX, out int dirtyY, out int dirtyWidth, out int dirtyHeight, out int fbWidth, out int fbHeight) =>
            {
                presentCallCount++;
                dirtyX = 0;
                dirtyY = 0;
                dirtyWidth = 0;
                dirtyHeight = 0;
                fbWidth = 0;
                fbHeight = 0;
                return false;
            },
            initializeBitmap: false);

        presenter.EnqueueFrame(10, 10);
        presenter.Dispose();
        AvaloniaTestEnvironment.RunPendingDispatcherJobs();

        Assert.Equal(0, setScreenCount);
        Assert.Equal(0, presentCallCount);
        Assert.Equal(0, redrawCount);
    }
}

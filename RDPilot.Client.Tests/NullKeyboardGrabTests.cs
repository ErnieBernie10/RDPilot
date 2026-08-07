using System;
using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Services;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class NullKeyboardGrabTests
{
    [Fact]
    public void NullKeyboardGrab_ReportsUnsupportedWithAReason()
    {
        using var grab = new NullKeyboardGrab();

        Assert.False(grab.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(grab.UnsupportedReason));
    }

    [Fact]
    public void SetEngaged_OnUnsupportedPlatform_NeverEngages()
    {
        using var grab = new NullKeyboardGrab();
        grab.Attach(new IntPtr(0x1234));

        grab.SetEngaged(true);

        Assert.False(grab.IsEngaged);
    }
}

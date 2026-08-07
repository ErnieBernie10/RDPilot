using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Services;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class KeyboardGrabScanCodeMapperTests
{
    private const uint VirtualKeyTab = 0x09;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeySnapshot = 0x2C;
    private const uint VirtualKeyPause = 0x13;

    private const uint FlagExtended = 0x01;
    private const uint FlagUp = 0x80;

    [Fact]
    public void TryMapHookEvent_PlainKeyDown_UsesHardwareScanCode()
    {
        Assert.True(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeyTab, 0x0F, 0, out var scancode, out var extended, out var isUp));

        Assert.Equal((ushort)0x0F, scancode);
        Assert.False(extended);
        Assert.False(isUp);
    }

    [Fact]
    public void TryMapHookEvent_WindowsKeyRelease_IsExtendedAndUp()
    {
        Assert.True(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeyLeftWindows, 0x5B, FlagExtended | FlagUp, out var scancode, out var extended, out var isUp));

        Assert.Equal((ushort)0x5B, scancode);
        Assert.True(extended);
        Assert.True(isUp);
    }

    [Fact]
    public void TryMapHookEvent_PrintScreen_NormalizesToExtended37()
    {
        Assert.True(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeySnapshot, 0x54, 0, out var scancode, out var extended, out _));

        Assert.Equal((ushort)0x37, scancode);
        Assert.True(extended);
    }

    [Fact]
    public void TryMapHookEvent_Pause_DropsExtendedBecauseExtended1IsUnsupported()
    {
        Assert.True(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeyPause, 0x45, FlagExtended, out var scancode, out var extended, out _));

        Assert.Equal((ushort)0x45, scancode);
        Assert.False(extended);
    }

    [Fact]
    public void TryMapHookEvent_ZeroScanCode_IsRejected()
    {
        Assert.False(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeyTab, 0, 0, out var scancode, out _, out _));

        Assert.Equal((ushort)0, scancode);
    }

    [Fact]
    public void TryMapHookEvent_ScanCodeAboveByteRange_IsMasked()
    {
        Assert.True(KeyboardGrabScanCodeMapper.TryMapHookEvent(
            VirtualKeyTab, 0x1F0F, 0, out var scancode, out _, out _));

        Assert.Equal((ushort)0x0F, scancode);
    }
}

using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RdpKeyboardInputMapperTests
{
    [Fact]
    public void TryMapKey_MapsCommonAndExtendedKeys()
    {
        Assert.True(RdpKeyboardInputMapper.TryMapKey(Key.A, out var a));
        Assert.Equal((ushort)0x1E, a);

        Assert.True(RdpKeyboardInputMapper.TryMapKey(Key.RightAlt, out var rightAlt));
        Assert.Equal((ushort)0x138, rightAlt);

        Assert.False(RdpKeyboardInputMapper.TryMapKey(Key.None, out var none));
        Assert.Equal((ushort)0, none);
    }

    [Fact]
    public void BuildKeyFlags_SeparatesExtendedBitAndReleaseBit()
    {
        var flags = RdpKeyboardInputMapper.BuildKeyFlags(0x138, isRelease: false, out var normalized);
        Assert.Equal((ushort)0x0100, flags);
        Assert.Equal((ushort)0x38, normalized);

        flags = RdpKeyboardInputMapper.BuildKeyFlags(0x14D, isRelease: true, out normalized);
        Assert.Equal((ushort)0x8100, flags);
        Assert.Equal((ushort)0x4D, normalized);
    }
}

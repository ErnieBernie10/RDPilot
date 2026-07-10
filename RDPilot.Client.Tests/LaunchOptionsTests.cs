using RDPilot.Client.Services;
using Xunit;

namespace RDPilot.Client.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class LaunchOptionsTests
{
    [Fact]
    public void Parse_ConnectArgument_ReturnsConnectionId()
    {
        var options = LaunchOptions.Parse(["--connect", "profile-id"]);

        Assert.Equal("profile-id", options.ConnectionId);
    }

    [Fact]
    public void Parse_WithoutConnectArgument_ReturnsEmptyOptions()
    {
        var options = LaunchOptions.Parse(["--other", "value"]);

        Assert.Null(options.ConnectionId);
    }
}

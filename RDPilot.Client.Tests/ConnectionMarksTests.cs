using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Models;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ConnectionMarksTests
{
    [Theory]
    [InlineData("SRV-004", "S4")]
    [InlineData("SRV-005", "S5")]
    [InlineData("DAH-SCANPC-003.workplace.local", "D3")]
    [InlineData("Build agent", "BA")]
    [InlineData("Desktop", "DE")]
    [InlineData("127.0.0.1", "1")]
    [InlineData("192.168.4.254", "254")]
    [InlineData("SRV-000", "S0")]
    [InlineData("", "?")]
    public void ToMonogram_KeepsNumberedMachinesApart(string name, string expected)
    {
        Assert.Equal(expected, ConnectionMarks.ToMonogram(name));
    }

    [Fact]
    public void ToAddress_ShowsUserAtHost_AndOnlyNonDefaultPorts()
    {
        Assert.Equal("admin@file-01", ConnectionMarks.ToAddress(new SavedConnection { Name = "File", Host = "file-01", Username = "admin" }));
        Assert.Equal("file-01:3390", ConnectionMarks.ToAddress(new SavedConnection { Name = "File", Host = "file-01", Port = 3390 }));
    }

    [Fact]
    public void ToAddress_DoesNotRepeatAHostThatIsAlsoTheName()
    {
        Assert.Equal("arneb", ConnectionMarks.ToAddress(new SavedConnection { Name = "127.0.0.1", Host = "127.0.0.1", Username = "arneb" }));
        Assert.Equal("", ConnectionMarks.ToAddress(new SavedConnection { Name = "pc", Host = "PC" }));
    }
}

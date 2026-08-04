using System.Diagnostics.CodeAnalysis;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ConnectionEditorViewModelTests
{
    [Fact]
    public void NewConnection_DefaultsPortTo3389()
    {
        var vm = new ConnectionEditorViewModel();

        Assert.Equal("3389", vm.Port);
    }

    [Fact]
    public void ExistingConnection_LoadsAndPreservesPort()
    {
        var vm = new ConnectionEditorViewModel(new SavedConnection
        {
            Name = "Lab",
            Host = "rdp.example.test",
            Port = 3390
        });

        var result = Assert.IsType<ConnectionEditResult>(vm.BuildResult());

        Assert.Equal("3390", vm.Port);
        Assert.Equal((ushort)3390, result.Connection.Port);
    }

    [Fact]
    public void BuildResult_ValidPort_PersistsPort()
    {
        var vm = new ConnectionEditorViewModel
        {
            Name = "Lab",
            Host = "rdp.example.test",
            Port = "3390"
        };

        var result = Assert.IsType<ConnectionEditResult>(vm.BuildResult());

        Assert.Equal((ushort)3390, result.Connection.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("rdp")]
    public void BuildResult_InvalidPort_ReportsValidationError(string port)
    {
        var vm = new ConnectionEditorViewModel
        {
            Name = "Lab",
            Host = "rdp.example.test",
            Port = port
        };

        var result = vm.BuildResult();

        Assert.Null(result);
        Assert.Equal("Port must be a number from 1 to 65535.", vm.ValidationMessage);
    }
}

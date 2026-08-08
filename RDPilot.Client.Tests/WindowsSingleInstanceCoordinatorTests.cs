using RDPilot.Client.Services;
using Xunit;

namespace RDPilot.Client.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class WindowsSingleInstanceCoordinatorTests
{
    [Fact]
    public async Task CreateAsync_OnNonWindows_ReturnsPrimaryInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var coordinator = await WindowsSingleInstanceCoordinator.CreateAsync(
            LaunchOptions.Parse([]),
            _ => Task.CompletedTask);

        Assert.True(coordinator.IsPrimaryInstance);
    }
}

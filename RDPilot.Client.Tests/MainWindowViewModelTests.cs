using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task CloseSelectedSession_SelectsNeighbor()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory(
            new[]
            {
                new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected),
                new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected)
            });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, CreateConnection("One"), secretStore);
        await ConnectAsync(vm, CreateConnection("Two"), secretStore);

        var first = vm.Sessions[0];
        var second = vm.Sessions[1];
        vm.SelectedSession = first;

        await vm.CloseSessionCommand.ExecuteAsync(first);

        Assert.Single(vm.Sessions);
        Assert.Same(second, vm.SelectedSession);
    }

    [Fact]
    public async Task CloseNonSelectedSession_PreservesSelection()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory(
            new[]
            {
                new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected),
                new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected)
            });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, CreateConnection("One"), secretStore);
        await ConnectAsync(vm, CreateConnection("Two"), secretStore);

        var first = vm.Sessions[0];
        var second = vm.Sessions[1];
        vm.SelectedSession = second;

        await vm.CloseSessionCommand.ExecuteAsync(first);

        Assert.Single(vm.Sessions);
        Assert.Same(second, vm.SelectedSession);
    }

    [Fact]
    public async Task ReconnectSession_ReplacesSessionInPlace()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var originalConnection = CreateConnection("Reconnect Me");
        var oldSession = new RdpSessionViewModel(originalConnection, RdpSessionStatus.Failed,
            new RdpSessionError(1, "FREERDP_ERROR_CONNECT_FAILED", "Connection failed.", RdpSessionErrorKind.TimeoutOrTransport));
        var replacement = new RdpSessionViewModel(originalConnection, RdpSessionStatus.Connecting);
        var factory = new QueueSessionFactory(new[] { replacement });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();
        vm.Sessions.Add(oldSession);
        vm.SelectedSession = oldSession;
        secretStore.SetPassword(originalConnection.Id, "pw", "gw");

        await vm.ReconnectSessionCommand.ExecuteAsync(oldSession);

        Assert.Single(vm.Sessions);
        Assert.Same(replacement, vm.Sessions[0]);
        Assert.Same(replacement, vm.SelectedSession);
    }

    [Fact]
    public void SessionStatus_UpdatesDisconnectAndReconnectState()
    {
        var connection = CreateConnection("State Test");
        var session = new RdpSessionViewModel(connection, RdpSessionStatus.Connecting);

        Assert.True(session.CanDisconnect);
        Assert.False(session.CanReconnect);

        session.SetTestStatus(RdpSessionStatus.Failed,
            new RdpSessionError(1, "FREERDP_ERROR_CONNECT_FAILED", "Connection failed.", RdpSessionErrorKind.TimeoutOrTransport));

        Assert.False(session.CanDisconnect);
        Assert.True(session.CanReconnect);
        Assert.Equal("Connection failed.", session.ErrorText);
    }

    [Fact]
    public async Task SaveConnection_DoesNotPersistPasswordsInConnectionsFile()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var connection = CreateConnection("Persist Test");

        await store.SaveAsync(connection, "top-secret", true, "gateway-secret", true);

        var json = await File.ReadAllTextAsync(AppDataPaths.ConnectionsFilePath);

        Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-secret", json, StringComparison.Ordinal);
        Assert.Equal("top-secret", await secretStore.GetSecretAsync(SecretStore.PasswordKey(connection.Id)));
        Assert.Equal("gateway-secret", await secretStore.GetSecretAsync(SecretStore.GatewayPasswordKey(connection.Id)));
    }

    private static async Task ConnectAsync(MainWindowViewModel vm, SavedConnection connection, FakeSecretStore secretStore)
    {
        secretStore.SetPassword(connection.Id, "pw", "gw");
        vm.Connections.Add(connection);
        vm.SelectedConnection = connection;
        await vm.ConnectCommand.ExecuteAsync(null);
    }

    private static SavedConnection CreateConnection(string name)
    {
        return new SavedConnection
        {
            Name = name,
            Host = $"{name.ToLowerInvariant().Replace(' ', '-')}.example.local",
            Username = "user"
        };
    }

    private sealed class QueueSessionFactory : IRdpSessionFactory
    {
        private readonly Queue<RdpSessionViewModel> _sessions;

        public QueueSessionFactory(IEnumerable<RdpSessionViewModel> sessions)
        {
            _sessions = new Queue<RdpSessionViewModel>(sessions);
        }

        public RdpSessionViewModel Create(SavedConnection connection, string password, string gatewayPassword, int width, int height, Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
        {
            return _sessions.Dequeue();
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string Description => "Fake";

        public Task<string?> GetSecretAsync(string key)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult<string?>(value);
        }

        public Task SetSecretAsync(string key, string secret)
        {
            _values[key] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public void SetPassword(string connectionId, string password, string gatewayPassword)
        {
            _values[SecretStore.PasswordKey(connectionId)] = password;
            _values[SecretStore.GatewayPasswordKey(connectionId)] = gatewayPassword;
        }
    }

    private sealed class TestConfigHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TestConfigHome()
        {
            _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            _path = Path.Combine(Path.GetTempPath(), "rdp-client-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, true);
            }
        }
    }
}

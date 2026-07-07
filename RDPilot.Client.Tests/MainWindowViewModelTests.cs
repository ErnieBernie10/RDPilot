using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
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

    [Fact]
    public async Task SaveConnection_PersistsQualityOverrides()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var connection = CreateConnection("Quality Persist");
        connection.QualityOverrides = new RdpQualitySettings
        {
            ColorDepth = 32,
            FontSmoothing = true,
            DesktopWallpaper = false,
            ConnectionType = RdpConnectionType.Lan
        };

        await store.SaveAsync(connection, null, false, null, false);
        var loaded = Assert.Single(await store.LoadAsync(), c => c.Id == connection.Id);

        Assert.NotNull(loaded.QualityOverrides);
        Assert.Equal(32, loaded.QualityOverrides.ColorDepth);
        Assert.True(loaded.QualityOverrides.FontSmoothing);
        Assert.False(loaded.QualityOverrides.DesktopWallpaper);
        Assert.Equal(RdpConnectionType.Lan, loaded.QualityOverrides.ConnectionType);
    }

    [Fact]
    public void QualityDefaults_ResolveUsesPropertyOverridesOnlyWhenSet()
    {
        var global = new RdpQualitySettings
        {
            ColorDepth = 24,
            FontSmoothing = true,
            DesktopWallpaper = true,
            Themes = true,
            MenuAnimations = true,
            FullWindowDrag = true,
            Compression = false,
            BitmapCache = false,
            ConnectionType = RdpConnectionType.Lan
        };
        var overrides = new RdpQualitySettings
        {
            ColorDepth = 32,
            FontSmoothing = false,
            ConnectionType = RdpConnectionType.Wan
        };

        var resolved = RdpQualityDefaults.Resolve(global, overrides);

        Assert.Equal(32, resolved.ColorDepth);
        Assert.False(resolved.FontSmoothing);
        Assert.True(resolved.DesktopWallpaper);
        Assert.True(resolved.Themes);
        Assert.True(resolved.MenuAnimations);
        Assert.True(resolved.FullWindowDrag);
        Assert.False(resolved.Compression);
        Assert.False(resolved.BitmapCache);
        Assert.Equal(RdpConnectionType.Wan, resolved.ConnectionType);
    }

    [Fact]
    public async Task ConnectSession_UsesResolvedQualitySettings()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var settingsStore = new AppSettingsStore();
        await settingsStore.SaveAsync(new AppSettings
        {
            QualitySettings = new RdpQualitySettings
            {
                ColorDepth = 24,
                FontSmoothing = true,
                Compression = false,
                ConnectionType = RdpConnectionType.Lan
            }
        });

        var connection = CreateConnection("Quality Connect");
        connection.QualityOverrides = new RdpQualitySettings
        {
            FontSmoothing = false,
            BitmapCache = false
        };
        var session = new RdpSessionViewModel(connection, RdpSessionStatus.Connected);
        var factory = new QueueSessionFactory(new[] { session });
        var vm = new MainWindowViewModel(store, settingsStore, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, connection, secretStore);

        Assert.Equal(session, vm.SelectedSession);
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

        public RdpSessionViewModel Create(SavedConnection connection, string password, string gatewayPassword, int width, int height, double renderScaling, int colorDepth, bool compression, bool fontSmoothing, bool bitmapCache, bool desktopWallpaper, bool themes, bool menuAnimations, bool fullWindowDrag, RdpConnectionType connectionType, Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
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
            _previous = Environment.GetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable);
            _path = Path.Combine("C:\\Temp", "rdp-client-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _previous);
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, true);
            }
        }
    }
}

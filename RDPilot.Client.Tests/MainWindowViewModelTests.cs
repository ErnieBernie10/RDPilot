using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
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
    public async Task ConnectById_SelectsAndConnectsSavedConnection()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var connection = CreateConnection("Direct Connect");
        await store.SaveAsync(connection, "pw", true, null, false);
        var session = new RdpSessionViewModel(connection, RdpSessionStatus.Connecting);
        var vm = new MainWindowViewModel(store, new QueueSessionFactory([session]));
        await vm.LoadConnectionsAsync();

        await vm.ConnectByIdAsync(connection.Id);

        Assert.NotNull(vm.SelectedConnection);
        Assert.Equal(connection.Id, vm.SelectedConnection.Id);
        Assert.Same(session, vm.SelectedSession);
    }

    [Fact]
    public async Task LoadConnections_DoesNotSelectFirstSavedConnection()
    {
        using var env = new TestConfigHome();
        var store = new ConnectionStore(new FakeSecretStore());
        await store.SaveAsync(CreateConnection("First"), "pw", true, null, false);
        await store.SaveAsync(CreateConnection("Second"), "pw", true, null, false);
        var vm = new MainWindowViewModel(store, new QueueSessionFactory([]));

        await vm.LoadConnectionsAsync();

        Assert.Null(vm.SelectedConnection);
        Assert.False(vm.CanShowConnectAction);
        Assert.True(vm.CanShowConnectionsAction);
        Assert.Equal("Open Connections to choose a saved connection.", vm.ViewportDetail);
    }

    [Fact]
    public async Task ConnectById_UnknownConnection_ReportsMissingProfile()
    {
        using var env = new TestConfigHome();
        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new QueueSessionFactory([]));
        await vm.LoadConnectionsAsync();

        await vm.ConnectByIdAsync("missing");

        Assert.Empty(vm.Sessions);
        Assert.Equal("The requested saved connection no longer exists.", vm.StatusMessage);
    }

    [Fact]
    public async Task IsKeyboardGrabActive_MirrorsTheSelectedSessionGrabState()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory([new RdpSessionViewModel(CreateConnection("Grabbable"), RdpSessionStatus.Connected)]);
        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();
        await ConnectAsync(vm, CreateConnection("Grabbable"), secretStore);

        Assert.False(vm.IsKeyboardGrabActive);

        vm.SelectedSession!.IsKeyboardGrabbed = true;
        Assert.True(vm.IsKeyboardGrabActive);

        vm.ReleaseKeyboardGrab();
        Assert.False(vm.IsKeyboardGrabActive);
        Assert.False(vm.SelectedSession.IsKeyboardGrabbed);
    }

    [Fact]
    public async Task ToggleKeyboardGrab_WithoutPlatformSupport_DoesNothing()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory([new RdpSessionViewModel(CreateConnection("Ungrabbable"), RdpSessionStatus.Connected)]);
        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();
        await ConnectAsync(vm, CreateConnection("Ungrabbable"), secretStore);
        vm.IsKeyboardGrabSupported = false;

        vm.ToggleKeyboardGrabCommand.Execute(null);

        Assert.False(vm.IsKeyboardGrabActive);
        Assert.False(vm.SelectedSession!.IsKeyboardGrabbed);
    }

    [Fact]
    public async Task ToggleKeyboardGrab_WithPlatformSupport_FlipsTheSelectedSession()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory([new RdpSessionViewModel(CreateConnection("Toggleable"), RdpSessionStatus.Connected)]);
        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();
        await ConnectAsync(vm, CreateConnection("Toggleable"), secretStore);
        vm.IsKeyboardGrabSupported = true;
        MarkSessionNativeHandleLive(vm.SelectedSession!);

        vm.ToggleKeyboardGrabCommand.Execute(null);
        Assert.True(vm.IsKeyboardGrabActive);

        vm.ToggleKeyboardGrabCommand.Execute(null);
        Assert.False(vm.IsKeyboardGrabActive);
    }

    [Fact]
    public async Task ToggleKeyboardGrab_SessionWithoutLiveHandle_DoesNothing()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var factory = new QueueSessionFactory([new RdpSessionViewModel(CreateConnection("Not Live"), RdpSessionStatus.Connected)]);
        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();
        await ConnectAsync(vm, CreateConnection("Not Live"), secretStore);
        vm.IsKeyboardGrabSupported = true;

        vm.ToggleKeyboardGrabCommand.Execute(null);

        Assert.False(vm.IsKeyboardGrabActive);
    }

    private static void MarkSessionNativeHandleLive(RdpSessionViewModel session)
    {
        var field = typeof(RdpSessionViewModel).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(session, new IntPtr(0xC0DE));
    }

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
        connection.Port = 3390;

        await store.SaveAsync(connection, "top-secret", true, "gateway-secret", true);

        var json = await File.ReadAllTextAsync(AppDataPaths.ConnectionsFilePath);

        Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-secret", json, StringComparison.Ordinal);
        Assert.Contains("\"Port\": 3390", json, StringComparison.Ordinal);
        var loaded = Assert.Single(await store.LoadAsync());
        Assert.Equal((ushort)3390, loaded.Port);
        Assert.Equal("top-secret", await secretStore.GetSecretAsync(SecretStore.PasswordKey(connection.Id)));
        Assert.Equal("gateway-secret", await secretStore.GetSecretAsync(SecretStore.GatewayPasswordKey(connection.Id)));
    }

    [Fact]
    public async Task ImportLocalConnection_PreservesCustomPort()
    {
        using var env = new TestConfigHome();
        var localConnectionPath = Path.Combine(AppContext.BaseDirectory, "connection.local.json");
        var originalFile = File.Exists(localConnectionPath) ? await File.ReadAllTextAsync(localConnectionPath) : null;

        try
        {
            await File.WriteAllTextAsync(localConnectionPath, "{\"Host\":\"rdp.example.local\",\"Port\":3390}");
            var store = new ConnectionStore(new FakeSecretStore());
            var method = typeof(ConnectionStore).GetMethod("TryImportLocalConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var imported = await Assert.IsAssignableFrom<Task<List<SavedConnection>>>(method.Invoke(store, null));

            Assert.Equal((ushort)3390, Assert.Single(imported).Port);
        }
        finally
        {
            if (originalFile == null)
            {
                File.Delete(localConnectionPath);
            }
            else
            {
                await File.WriteAllTextAsync(localConnectionPath, originalFile);
            }
        }
    }

    [Fact]
    public async Task SaveConnection_RefreshesConnectionsImmediately()
    {
        using var env = new TestConfigHome();
        var vm = new MainWindowViewModel(new ConnectionStore(new FakeSecretStore()), new QueueSessionFactory([]));
        await vm.LoadConnectionsAsync();
        var connection = CreateConnection("New Connection");

        await vm.SaveConnectionAsync(new ConnectionEditResult { Connection = connection });

        var saved = Assert.Single(vm.Connections);
        Assert.Equal(connection.Id, saved.Id);
        Assert.Same(saved, vm.SelectedConnection);
    }

    [Fact]
    public async Task DeleteConnection_RefreshesConnectionsImmediately()
    {
        using var env = new TestConfigHome();
        var store = new ConnectionStore(new FakeSecretStore());
        var connection = CreateConnection("Delete Me");
        await store.SaveAsync(connection, null, false, null, false);
        var vm = new MainWindowViewModel(store, new QueueSessionFactory([]));
        await vm.LoadConnectionsAsync();
        vm.SelectedConnection = Assert.Single(vm.Connections);

        await vm.DeleteSelectedConnectionAsync();

        Assert.Empty(vm.Connections);
        Assert.Null(vm.SelectedConnection);
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
        Assert.Equal(24, factory.ColorDepth);
        Assert.False(factory.Compression);
        Assert.False(factory.FontSmoothing);
        Assert.False(factory.BitmapCache);
        Assert.False(factory.DesktopWallpaper);
        Assert.False(factory.Themes);
        Assert.False(factory.MenuAnimations);
        Assert.False(factory.FullWindowDrag);
        Assert.Equal(RdpConnectionType.Lan, factory.ConnectionType);
    }

    [Fact]
    public async Task SelectedSessionStatusChange_UpdatesShellStateWhileBackgroundChangeDoesNot()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var first = new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected);
        var second = new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected);
        var factory = new QueueSessionFactory(new[] { first, second });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, first.Connection, secretStore);
        await ConnectAsync(vm, second.Connection, secretStore);
        vm.SelectedSession = first;

        first.SetTestStatus(RdpSessionStatus.Disconnected);

        Assert.False(vm.IsConnected);
        Assert.False(vm.SelectedSessionCanDisconnect);
        Assert.True(vm.SelectedSessionCanReconnect);
        Assert.Equal("Disconnected from One.", vm.StatusMessage);

        second.SetTestStatus(
            RdpSessionStatus.Failed,
            new RdpSessionError(1, "FREERDP_ERROR_CONNECT_FAILED", "background failure", RdpSessionErrorKind.TimeoutOrTransport));

        Assert.False(vm.IsConnected);
        Assert.False(vm.SelectedSessionCanDisconnect);
        Assert.True(vm.SelectedSessionCanReconnect);
        Assert.Equal("Disconnected from One.", vm.StatusMessage);
    }

    [Fact]
    public async Task SessionRedrawRequested_RaisesOnlyForSelectedSession()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var first = new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected);
        var second = new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected);
        var factory = new QueueSessionFactory(new[] { first, second });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, first.Connection, secretStore);
        await ConnectAsync(vm, second.Connection, secretStore);
        vm.SelectedSession = first;

        var redrawCount = 0;
        RdpSessionViewModel? raisedSession = null;
        vm.SessionRedrawRequested += (_, session) =>
        {
            redrawCount++;
            raisedSession = session;
        };

        InvokePrivate(vm, "OnSessionRequestRedraw", second, EventArgs.Empty);
        Assert.Equal(0, redrawCount);

        InvokePrivate(vm, "OnSessionRequestRedraw", first, EventArgs.Empty);
        Assert.Equal(1, redrawCount);
        Assert.Same(first, raisedSession);
    }

    [Fact]
    public async Task RemoteClipboardTextReceived_RaisesOnlyForSelectedSession()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var first = new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected);
        var second = new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected);
        var factory = new QueueSessionFactory(new[] { first, second });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, first.Connection, secretStore);
        await ConnectAsync(vm, second.Connection, secretStore);
        vm.SelectedSession = first;

        var received = new List<(RdpSessionViewModel Session, string Text)>();
        vm.RemoteClipboardTextReceived += (_, value) => received.Add(value);

        InvokePrivate(vm, "OnRemoteClipboardTextReceived", second, "background");
        Assert.Empty(received);

        InvokePrivate(vm, "OnRemoteClipboardTextReceived", first, "selected");
        var clipboardEvent = Assert.Single(received);
        Assert.Same(first, clipboardEvent.Session);
        Assert.Equal("selected", clipboardEvent.Text);
    }

    [Fact]
    public async Task RemoteClipboardFilesReceived_RaisesOnlyForSelectedSession()
    {
        using var env = new TestConfigHome();
        var secretStore = new FakeSecretStore();
        var store = new ConnectionStore(secretStore);
        var first = new RdpSessionViewModel(CreateConnection("One"), RdpSessionStatus.Connected);
        var second = new RdpSessionViewModel(CreateConnection("Two"), RdpSessionStatus.Connected);
        var factory = new QueueSessionFactory(new[] { first, second });

        var vm = new MainWindowViewModel(store, factory);
        await vm.LoadConnectionsAsync();

        await ConnectAsync(vm, first.Connection, secretStore);
        await ConnectAsync(vm, second.Connection, secretStore);
        vm.SelectedSession = first;

        var received = new List<(RdpSessionViewModel Session, string[] FilePaths)>();
        vm.RemoteClipboardFilesReceived += (_, value) => received.Add(value);

        InvokePrivate(vm, "OnRemoteClipboardFilesReceived", second, new[] { "background.txt" });
        Assert.Empty(received);

        InvokePrivate(vm, "OnRemoteClipboardFilesReceived", first, new[] { "selected.txt" });
        var clipboardEvent = Assert.Single(received);
        Assert.Same(first, clipboardEvent.Session);
        Assert.Equal(["selected.txt"], clipboardEvent.FilePaths);
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
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

        public int ColorDepth { get; private set; }
        public bool Compression { get; private set; }
        public bool FontSmoothing { get; private set; }
        public bool BitmapCache { get; private set; }
        public bool DesktopWallpaper { get; private set; }
        public bool Themes { get; private set; }
        public bool MenuAnimations { get; private set; }
        public bool FullWindowDrag { get; private set; }
        public RdpConnectionType ConnectionType { get; private set; }

        public QueueSessionFactory(IEnumerable<RdpSessionViewModel> sessions)
        {
            _sessions = new Queue<RdpSessionViewModel>(sessions);
        }

        public RdpSessionViewModel Create(SavedConnection connection, string password, string gatewayPassword, int width, int height, double renderScaling, int colorDepth, bool compression, bool fontSmoothing, bool bitmapCache, bool desktopWallpaper, bool themes, bool menuAnimations, bool fullWindowDrag, RdpConnectionType connectionType, Action<RdpSessionViewModel, string> remoteClipboardTextReceived, Action<RdpSessionViewModel, string[]> remoteClipboardFilesReceived)
        {
            ColorDepth = colorDepth;
            Compression = compression;
            FontSmoothing = fontSmoothing;
            BitmapCache = bitmapCache;
            DesktopWallpaper = desktopWallpaper;
            Themes = themes;
            MenuAnimations = menuAnimations;
            FullWindowDrag = fullWindowDrag;
            ConnectionType = connectionType;
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

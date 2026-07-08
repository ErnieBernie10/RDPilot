using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDPilot.Client.Models;
using RDPilot.Client.Services;

namespace RDPilot.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private SavedConnection? _selectedConnection;
    [ObservableProperty] private RdpSessionViewModel? _selectedSession;
    [ObservableProperty] private string _statusMessage = "Loading saved connections...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _selectedSessionCanDisconnect;
    [ObservableProperty] private bool _selectedSessionCanReconnect;
    [ObservableProperty] private ShellPanel _activeShellPanel;

    private readonly ConnectionStore _connectionStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly IRdpSessionFactory _sessionFactory;
    private AppSettings _settings = new();
    private int _requestedWidth = 1280;
    private int _requestedHeight = 720;
    private double _renderScaling = 1.0;

    public ObservableCollection<SavedConnection> Connections { get; } = new();
    public ObservableCollection<RdpSessionViewModel> Sessions { get; } = new();
    public bool IsConnectionsPanelOpen => ActiveShellPanel == ShellPanel.Connections;
    public string ConnectionsFilePath => _connectionStore.ConnectionsFilePath;
    public string SettingsFilePath => _settingsStore.SettingsFilePath;
    public string SecretStoreDescription => _connectionStore.SecretStoreDescription;
    public event EventHandler<RdpSessionViewModel>? SessionRedrawRequested;
    public event EventHandler<(RdpSessionViewModel Session, string Text)>? RemoteClipboardTextReceived;
    public event EventHandler<(RdpSessionViewModel Session, string[] FilePaths)>? RemoteClipboardFilesReceived;

    public MainWindowViewModel() : this(new ConnectionStore(SecretStore.CreateDefault()), new AppSettingsStore(), new RdpSessionFactory())
    {
    }

    public MainWindowViewModel(ConnectionStore connectionStore) : this(connectionStore, new AppSettingsStore(), new RdpSessionFactory())
    {
    }

    public MainWindowViewModel(ConnectionStore connectionStore, IRdpSessionFactory sessionFactory) : this(connectionStore, new AppSettingsStore(), sessionFactory)
    {
    }

    public MainWindowViewModel(ConnectionStore connectionStore, AppSettingsStore settingsStore, IRdpSessionFactory sessionFactory)
    {
        _connectionStore = connectionStore;
        _settingsStore = settingsStore;
        _sessionFactory = sessionFactory;
        _ = LoadConnectionsAsync();
    }

    public async Task LoadConnectionsAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            var connections = await _connectionStore.LoadAsync();
            Connections.Clear();
            foreach (var connection in connections)
            {
                Connections.Add(connection);
            }

            SelectedConnection = Connections.FirstOrDefault();
            StatusMessage = Connections.Count == 0
                ? "Add a connection to get started."
                : $"Loaded {Connections.Count} saved connection{(Connections.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to load saved connections: {ex.Message}";
        }
    }

    public AppSettings CreateSettingsSnapshot()
    {
        return _settings.Clone();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        try
        {
            _settings = settings.Clone();
            await _settingsStore.SaveAsync(_settings);
            StatusMessage = "Saved global settings.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to save global settings: {ex.Message}";
        }
    }

    public async Task SaveConnectionAsync(ConnectionEditResult result)
    {
        try
        {
            await _connectionStore.SaveAsync(
                result.Connection,
                result.Password,
                result.PasswordChanged,
                result.GatewayPassword,
                result.GatewayPasswordChanged);

            await LoadConnectionsAsync();
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == result.Connection.Id) ?? Connections.FirstOrDefault();
            StatusMessage = $"Saved {result.Connection.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to save {result.Connection.Name}: {ex.Message}";
        }
    }

    public async Task DeleteSelectedConnectionAsync()
    {
        if (SelectedConnection == null) return;

        try
        {
            var deletedName = SelectedConnection.Name;
            await _connectionStore.DeleteAsync(SelectedConnection);
            await LoadConnectionsAsync();
            StatusMessage = $"Deleted {deletedName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to delete connection: {ex.Message}";
        }
    }

    private bool CanConnect() => SelectedConnection != null;

    partial void OnSelectedConnectionChanged(SavedConnection? value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveShellPanelChanged(ShellPanel value)
    {
        OnPropertyChanged(nameof(IsConnectionsPanelOpen));
    }

    partial void OnSelectedSessionChanged(RdpSessionViewModel? value)
    {
        UpdateSelectedSessionState();
        if (value != null)
        {
            value.UpdateResolution(_requestedWidth, _requestedHeight, _renderScaling);
            StatusMessage = $"Active session: {value.Title}.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedConnection == null) return;

        var connection = SelectedConnection;
        string password;
        string gatewayPassword;

        try
        {
            password = await _connectionStore.GetPasswordAsync(connection) ?? "";
            gatewayPassword = await _connectionStore.GetGatewayPasswordAsync(connection) ?? "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to read password from {SecretStoreDescription}: {ex.Message}";
            return;
        }

        StatusMessage = $"Opening {connection.Name}...";
        var session = CreateSession(
            connection,
            password,
            gatewayPassword,
            OnRemoteClipboardTextReceived,
            OnRemoteClipboardFilesReceived);

        SubscribeSession(session);
        Sessions.Add(session);
        SelectedSession = session;
        ActiveShellPanel = ShellPanel.None;
        StatusMessage = $"Connecting to {connection.Name}...";
    }

    [RelayCommand]
    private void ToggleConnectionsPanel()
    {
        ActiveShellPanel = ActiveShellPanel == ShellPanel.Connections ? ShellPanel.None : ShellPanel.Connections;
    }

    [RelayCommand]
    private void CloseConnectionsPanel()
    {
        ActiveShellPanel = ShellPanel.None;
    }

    private static bool CanDisconnectSession(RdpSessionViewModel? session) => session?.CanDisconnect == true;
    private static bool CanReconnectSession(RdpSessionViewModel? session) => session?.CanReconnect == true;
    private static bool CanCloseSession(RdpSessionViewModel? session) => session != null;

    [RelayCommand(CanExecute = nameof(CanDisconnectSession))]
    private async Task DisconnectSessionAsync(RdpSessionViewModel? session)
    {
        if (session == null) return;

        await session.DisconnectAsync();
        if (ReferenceEquals(session, SelectedSession))
        {
            IsConnected = false;
            UpdateStatusMessageFromSelectedSession();
        }
    }

    [RelayCommand(CanExecute = nameof(CanReconnectSession))]
    private async Task ReconnectSessionAsync(RdpSessionViewModel? session)
    {
        if (session == null) return;

        var oldSession = session;
        var index = Sessions.IndexOf(oldSession);
        if (index < 0) return;
        var wasSelected = ReferenceEquals(oldSession, SelectedSession);

        string password;
        string gatewayPassword;
        try
        {
            password = await _connectionStore.GetPasswordAsync(oldSession.Connection) ?? "";
            gatewayPassword = await _connectionStore.GetGatewayPasswordAsync(oldSession.Connection) ?? "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to read password from {SecretStoreDescription}: {ex.Message}";
            return;
        }

        StatusMessage = $"Reconnecting {oldSession.Title}...";
        var newSession = CreateSession(oldSession.Connection, password, gatewayPassword, OnRemoteClipboardTextReceived, OnRemoteClipboardFilesReceived);
        SubscribeSession(newSession);
        UnsubscribeSession(oldSession);
        Sessions[index] = newSession;
        if (wasSelected)
        {
            SelectedSession = newSession;
        }
        oldSession.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanCloseSession))]
    private async Task CloseSessionAsync(RdpSessionViewModel? session)
    {
        if (session == null) return;

        var index = Sessions.IndexOf(session);
        if (index < 0) return;

        await session.DisconnectAsync();
        UnsubscribeSession(session);
        Sessions.Remove(session);
        session.Dispose();

        if (Sessions.Count == 0)
        {
            SelectedSession = null;
            IsConnected = false;
        }
        else
        {
            SelectedSession = Sessions[Math.Clamp(index, 0, Sessions.Count - 1)];
        }

        StatusMessage = $"Closed {session.Title}.";
    }

    public void UpdateResolution(int width, int height, double renderScaling = 0)
    {
        if (width <= 0 || height <= 0) return;
        if (renderScaling > 0) _renderScaling = renderScaling;
        _requestedWidth = width;
        _requestedHeight = height;
        SelectedSession?.UpdateResolution(width, height, _renderScaling);
    }

    public void SendMouseEventScaled(ushort flags, double dipX, double dipY)
    {
        SelectedSession?.SendMouseEventScaled(flags, dipX, dipY);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        SelectedSession?.SendKeyboardEvent(flags, code);
    }

    public void SetLocalClipboardText(string text)
    {
        SelectedSession?.SetLocalClipboardText(text);
    }

    public void SetLocalClipboardFiles(string[] filePaths)
    {
        SelectedSession?.SetLocalClipboardFiles(filePaths);
    }

    public void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height)
    {
        SelectedSession?.SetLocalClipboardBitmap(bitmapData, width, height);
    }

    private void OnSessionRequestRedraw(object? sender, EventArgs e)
    {
        if (sender is RdpSessionViewModel session && ReferenceEquals(session, SelectedSession))
        {
            SessionRedrawRequested?.Invoke(this, session);
        }
    }

    private void OnRemoteClipboardTextReceived(RdpSessionViewModel session, string text)
    {
        if (ReferenceEquals(session, SelectedSession))
        {
            RemoteClipboardTextReceived?.Invoke(this, (session, text));
        }
    }

    private void OnRemoteClipboardFilesReceived(RdpSessionViewModel session, string[] filePaths)
    {
        if (ReferenceEquals(session, SelectedSession))
        {
            RemoteClipboardFilesReceived?.Invoke(this, (session, filePaths));
        }
    }

    private RdpSessionViewModel CreateSession(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived,
        Action<RdpSessionViewModel, string[]> remoteClipboardFilesReceived)
    {
        var qualitySettings = RdpQualityDefaults.Resolve(_settings.QualitySettings, connection.QualityOverrides);

        return _sessionFactory.Create(
            connection,
            password,
            gatewayPassword,
            _requestedWidth,
            _requestedHeight,
            _renderScaling,
            qualitySettings.ColorDepth,
            qualitySettings.Compression,
            qualitySettings.FontSmoothing,
            qualitySettings.BitmapCache,
            qualitySettings.DesktopWallpaper,
            qualitySettings.Themes,
            qualitySettings.MenuAnimations,
            qualitySettings.FullWindowDrag,
            qualitySettings.ConnectionType,
            remoteClipboardTextReceived,
            remoteClipboardFilesReceived);
    }

    private void SubscribeSession(RdpSessionViewModel session)
    {
        session.RequestRedraw += OnSessionRequestRedraw;
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void UnsubscribeSession(RdpSessionViewModel session)
    {
        session.RequestRedraw -= OnSessionRequestRedraw;
        session.PropertyChanged -= OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpSessionViewModel.Status) or nameof(RdpSessionViewModel.StatusText) or nameof(RdpSessionViewModel.LastError) or nameof(RdpSessionViewModel.ErrorText) or nameof(RdpSessionViewModel.IsConnected) or nameof(RdpSessionViewModel.CanDisconnect) or nameof(RdpSessionViewModel.CanReconnect)
            && ReferenceEquals(sender, SelectedSession))
        {
            UpdateSelectedSessionState();
            UpdateStatusMessageFromSelectedSession();
        }
    }

    private void UpdateSelectedSessionState()
    {
        IsConnected = SelectedSession?.IsConnected == true;
        SelectedSessionCanDisconnect = SelectedSession?.CanDisconnect == true;
        SelectedSessionCanReconnect = SelectedSession?.CanReconnect == true;
        DisconnectSessionCommand.NotifyCanExecuteChanged();
        ReconnectSessionCommand.NotifyCanExecuteChanged();
        CloseSessionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStatusMessageFromSelectedSession()
    {
        if (SelectedSession == null) return;

        StatusMessage = SelectedSession.Status switch
        {
            RdpSessionStatus.Connected => $"Connected to {SelectedSession.Title}.",
            RdpSessionStatus.Connecting => $"Connecting to {SelectedSession.Title}...",
            RdpSessionStatus.Failed => SelectedSession.ErrorText is { Length: > 0 } error
                ? $"Failed to connect to {SelectedSession.Title}: {error}"
                : $"Failed to connect to {SelectedSession.Title}.",
            RdpSessionStatus.Disconnected => $"Disconnected from {SelectedSession.Title}.",
            RdpSessionStatus.Disconnecting => $"Disconnecting from {SelectedSession.Title}...",
            _ => StatusMessage
        };
    }

    public void Dispose()
    {
        foreach (var session in Sessions.ToArray())
        {
            UnsubscribeSession(session);
            session.Dispose();
        }

        Sessions.Clear();
        GC.SuppressFinalize(this);
    }
}

using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDP.Client.Models;
using RDP.Client.Services;

namespace RDP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private SavedConnection? _selectedConnection;
    [ObservableProperty] private RdpSessionViewModel? _selectedSession;
    [ObservableProperty] private string _statusMessage = "Loading saved connections...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _selectedSessionCanDisconnect;
    [ObservableProperty] private bool _selectedSessionCanReconnect;

    private readonly ConnectionStore _connectionStore;
    private int _requestedWidth = 1280;
    private int _requestedHeight = 720;

    public ObservableCollection<SavedConnection> Connections { get; } = new();
    public ObservableCollection<RdpSessionViewModel> Sessions { get; } = new();
    public string ConnectionsFilePath => _connectionStore.ConnectionsFilePath;
    public string SecretStoreDescription => _connectionStore.SecretStoreDescription;
    public event EventHandler<RdpSessionViewModel>? SessionRedrawRequested;
    public event EventHandler<(RdpSessionViewModel Session, string Text)>? RemoteClipboardTextReceived;

    public MainWindowViewModel() : this(new ConnectionStore(SecretStore.CreateDefault()))
    {
    }

    public MainWindowViewModel(ConnectionStore connectionStore)
    {
        _connectionStore = connectionStore;
        _ = LoadConnectionsAsync();
    }

    public async Task LoadConnectionsAsync()
    {
        try
        {
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

    partial void OnSelectedSessionChanged(RdpSessionViewModel? value)
    {
        UpdateSelectedSessionState();
        if (value != null)
        {
            value.UpdateResolution(_requestedWidth, _requestedHeight);
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
            OnRemoteClipboardTextReceived);

        SubscribeSession(session);
        Sessions.Add(session);
        SelectedSession = session;
        StatusMessage = $"Connecting to {connection.Name}...";
    }

    private bool HasSelectedSession() => SelectedSession != null;
    private bool CanDisconnectSelectedSession() => SelectedSession?.CanDisconnect == true;
    private bool CanReconnectSelectedSession() => SelectedSession?.CanReconnect == true;

    [RelayCommand(CanExecute = nameof(CanDisconnectSelectedSession))]
    private async Task DisconnectAsync()
    {
        if (SelectedSession == null) return;

        await SelectedSession.DisconnectAsync();
        IsConnected = false;
        StatusMessage = $"Disconnected from {SelectedSession.Title}.";
    }

    [RelayCommand(CanExecute = nameof(CanReconnectSelectedSession))]
    private async Task ReconnectSelectedSessionAsync()
    {
        if (SelectedSession == null) return;

        var oldSession = SelectedSession;
        var index = Sessions.IndexOf(oldSession);
        if (index < 0) return;

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
        var newSession = CreateSession(oldSession.Connection, password, gatewayPassword, OnRemoteClipboardTextReceived);
        SubscribeSession(newSession);
        UnsubscribeSession(oldSession);
        Sessions[index] = newSession;
        SelectedSession = newSession;
        oldSession.Dispose();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private async Task CloseSessionAsync()
    {
        if (SelectedSession == null) return;
        await CloseSessionAsync(SelectedSession);
    }

    public async Task CloseSessionAsync(RdpSessionViewModel session)
    {
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

    public void UpdateResolution(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _requestedWidth = width;
        _requestedHeight = height;
        SelectedSession?.UpdateResolution(width, height);
    }

    public void SendMouseEvent(ushort flags, ushort x, ushort y)
    {
        SelectedSession?.SendMouseEvent(flags, x, y);
    }

    public void SendKeyboardEvent(ushort flags, ushort code)
    {
        SelectedSession?.SendKeyboardEvent(flags, code);
    }

    public void SetLocalClipboardText(string text)
    {
        SelectedSession?.SetLocalClipboardText(text);
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

    private RdpSessionViewModel CreateSession(
        SavedConnection connection,
        string password,
        string gatewayPassword,
        Action<RdpSessionViewModel, string> remoteClipboardTextReceived)
    {
        return new RdpSessionViewModel(
            connection,
            password,
            gatewayPassword,
            _requestedWidth,
            _requestedHeight,
            remoteClipboardTextReceived);
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
        if (e.PropertyName is nameof(RdpSessionViewModel.Status) or nameof(RdpSessionViewModel.IsConnected) or nameof(RdpSessionViewModel.CanDisconnect) or nameof(RdpSessionViewModel.CanReconnect)
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
        DisconnectCommand.NotifyCanExecuteChanged();
        ReconnectSelectedSessionCommand.NotifyCanExecuteChanged();
        CloseSessionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStatusMessageFromSelectedSession()
    {
        if (SelectedSession == null) return;

        StatusMessage = SelectedSession.Status switch
        {
            "Connected" => $"Connected to {SelectedSession.Title}.",
            "Connecting" => $"Connecting to {SelectedSession.Title}...",
            "Failed" => $"Failed to connect to {SelectedSession.Title}.",
            "Disconnected" => $"Disconnected from {SelectedSession.Title}.",
            "Disconnecting" => $"Disconnecting from {SelectedSession.Title}...",
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
    }
}

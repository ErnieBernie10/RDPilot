using CommunityToolkit.Mvvm.ComponentModel;
using RDP.Client.Models;

namespace RDP.Client.ViewModels;

public partial class ConnectionEditorViewModel : ViewModelBase
{
    private readonly bool _isEdit;
    private readonly SavedConnection _original;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _gatewayHost = "";
    [ObservableProperty] private string _gatewayDomain = "";
    [ObservableProperty] private string _gatewayUsername = "";
    [ObservableProperty] private string _gatewayPassword = "";
    [ObservableProperty] private string _validationMessage = "";

    public ConnectionEditorViewModel(SavedConnection? connection = null)
    {
        _isEdit = connection != null;
        _original = connection?.Clone() ?? new SavedConnection();
        _title = _isEdit ? "Edit connection" : "Add connection";
        Name = _original.Name;
        Host = _original.Host;
        Domain = _original.Domain;
        Username = _original.Username;
        GatewayHost = _original.GatewayHost;
        GatewayDomain = _original.GatewayDomain;
        GatewayUsername = _original.GatewayUsername;
    }

    public string PasswordWatermark => _isEdit ? "Leave blank to keep existing password" : "Password";
    public string GatewayPasswordWatermark => _isEdit ? "Leave blank to keep existing gateway password" : "Gateway password";

    public ConnectionEditResult? BuildResult()
    {
        ValidationMessage = "";

        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = "Connection name is required.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            ValidationMessage = "Host is required.";
            return null;
        }

        var connection = _original.Clone();
        connection.Name = Name.Trim();
        connection.Host = Host.Trim();
        connection.Domain = Domain.Trim();
        connection.Username = Username.Trim();
        connection.GatewayHost = GatewayHost.Trim();
        connection.GatewayDomain = GatewayDomain.Trim();
        connection.GatewayUsername = GatewayUsername.Trim();

        return new ConnectionEditResult
        {
            Connection = connection,
            Password = Password,
            GatewayPassword = GatewayPassword,
            PasswordChanged = !_isEdit || Password.Length > 0,
            GatewayPasswordChanged = !_isEdit || GatewayPassword.Length > 0
        };
    }
}

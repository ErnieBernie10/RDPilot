using CommunityToolkit.Mvvm.ComponentModel;
using RDPilot.Client.Models;
using System.Globalization;

namespace RDPilot.Client.ViewModels;

public partial class ConnectionEditorViewModel : ViewModelBase
{
    private readonly bool _isEdit;
    private readonly SavedConnection _original;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "3389";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _gatewayHost = "";
    [ObservableProperty] private string _gatewayDomain = "";
    [ObservableProperty] private string _gatewayUsername = "";
    [ObservableProperty] private string _gatewayPassword = "";
    [ObservableProperty] private string _validationMessage = "";

    public ConnectionEditorViewModel(SavedConnection? connection = null, RdpQualitySettings? globalQualitySettings = null)
    {
        _isEdit = connection != null;
        _original = connection?.Clone() ?? new SavedConnection();
        _title = _isEdit ? "Edit connection" : "Add connection";
        Name = _original.Name;
        Host = _original.Host;
        Port = _original.Port.ToString(CultureInfo.InvariantCulture);
        Domain = _original.Domain;
        Username = _original.Username;
        GatewayHost = _original.GatewayHost;
        GatewayDomain = _original.GatewayDomain;
        GatewayUsername = _original.GatewayUsername;
        QualityEditor = new RdpQualitySettingsEditorViewModel(_original.QualityOverrides, allowInherit: true, globalQualitySettings);
    }

    public string PasswordWatermark => _isEdit ? "Leave blank to keep existing password" : "Password";
    public string GatewayPasswordWatermark => _isEdit ? "Leave blank to keep existing gateway password" : "Gateway password";
    public RdpQualitySettingsEditorViewModel QualityEditor { get; }

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

        if (!ushort.TryParse(Port.Trim(), out var port) || port == 0)
        {
            ValidationMessage = "Port must be a number from 1 to 65535.";
            return null;
        }

        var connection = _original.Clone();
        connection.Name = Name.Trim();
        connection.Host = Host.Trim();
        connection.Port = port;
        connection.Domain = Domain.Trim();
        connection.Username = Username.Trim();
        connection.GatewayHost = GatewayHost.Trim();
        connection.GatewayDomain = GatewayDomain.Trim();
        connection.GatewayUsername = GatewayUsername.Trim();
        connection.QualityOverrides = QualityEditor.HasAnyValue() ? QualityEditor.BuildSettings() : null;

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

namespace RDPilot.Client.Models;

public sealed class ConnectionEditResult
{
    public required SavedConnection Connection { get; init; }
    public string? Password { get; init; }
    public string? GatewayPassword { get; init; }
    public bool PasswordChanged { get; init; }
    public bool GatewayPasswordChanged { get; init; }
}

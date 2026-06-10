using System;

namespace RDP.Client.Models;

public sealed class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New connection";
    public string Host { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Username { get; set; } = "";
    public string GatewayHost { get; set; } = "";
    public string GatewayDomain { get; set; } = "";
    public string GatewayUsername { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SavedConnection Clone()
    {
        return new SavedConnection
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Domain = Domain,
            Username = Username,
            GatewayHost = GatewayHost,
            GatewayDomain = GatewayDomain,
            GatewayUsername = GatewayUsername,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}

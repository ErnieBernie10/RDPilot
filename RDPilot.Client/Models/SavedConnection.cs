using System;

namespace RDPilot.Client.Models;

public sealed class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New connection";
    public string Host { get; set; } = "";
    public ushort Port { get; set; } = 3389;
    public string Domain { get; set; } = "";
    public string Username { get; set; } = "";
    public string GatewayHost { get; set; } = "";
    public string GatewayDomain { get; set; } = "";
    public string GatewayUsername { get; set; } = "";
    public RdpQualitySettings? QualityOverrides { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SavedConnection Clone()
    {
        return new SavedConnection
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            Domain = Domain,
            Username = Username,
            GatewayHost = GatewayHost,
            GatewayDomain = GatewayDomain,
            GatewayUsername = GatewayUsername,
            QualityOverrides = QualityOverrides?.Clone(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}

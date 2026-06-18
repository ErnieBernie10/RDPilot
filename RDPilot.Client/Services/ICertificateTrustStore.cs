namespace RDPilot.Client.Services;

public interface ICertificateTrustStore
{
    string? GetTrustedFingerprint(string host, ushort port);
    void SaveTrustedFingerprint(string host, ushort port, string fingerprint);
}

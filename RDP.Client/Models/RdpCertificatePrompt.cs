namespace RDP.Client.Models;

public sealed record RdpCertificatePrompt(
    string Host,
    ushort Port,
    string CommonName,
    string Subject,
    string Issuer,
    string Fingerprint,
    bool IsChanged,
    string? PreviousSubject,
    string? PreviousIssuer,
    string? PreviousFingerprint);

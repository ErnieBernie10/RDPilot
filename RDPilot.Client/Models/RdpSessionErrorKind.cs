namespace RDPilot.Client.Models;

public enum RdpSessionErrorKind
{
    None,
    Dns,
    TimeoutOrTransport,
    Authentication,
    AccessDenied,
    CertificateOrTls,
    Gateway,
    Cancelled,
    Unknown
}

namespace RDP.Client.Models;

public sealed record RdpSessionError(
    uint NativeCode,
    string NativeName,
    string Message,
    RdpSessionErrorKind Kind)
{
    public static RdpSessionError? Create(uint nativeCode, string? nativeName, string? nativeMessage)
    {
        if (nativeCode == 0 && string.IsNullOrWhiteSpace(nativeName) && string.IsNullOrWhiteSpace(nativeMessage))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(nativeName) ? "Unknown" : nativeName;
        var message = string.IsNullOrWhiteSpace(nativeMessage) ? FriendlyMessageFor(name) : nativeMessage;
        return new RdpSessionError(nativeCode, name, message, KindFor(name));
    }

    private static RdpSessionErrorKind KindFor(string nativeName)
    {
        return nativeName switch
        {
            "FREERDP_ERROR_DNS_ERROR" or
            "FREERDP_ERROR_DNS_NAME_NOT_FOUND" => RdpSessionErrorKind.Dns,

            "FREERDP_ERROR_AUTHENTICATION_FAILED" or
            "FREERDP_ERROR_LOGON_FAILURE" or
            "FREERDP_ERROR_CONNECT_WRONG_PASSWORD" or
            "FREERDP_ERROR_CONNECT_PASSWORD_EXPIRED" or
            "FREERDP_ERROR_CONNECT_PASSWORD_MUST_CHANGE" => RdpSessionErrorKind.Authentication,

            "FREERDP_ERROR_ACCESS_DENIED" or
            "FREERDP_ERROR_INSUFFICIENT_PRIVILEGES" => RdpSessionErrorKind.AccessDenied,

            "FREERDP_ERROR_TLS_CONNECT_FAILED" or
            "FREERDP_ERROR_SECURITY_NEGO_CONNECT_FAILED" => RdpSessionErrorKind.CertificateOrTls,

            "FREERDP_ERROR_CONNECT_CANCELLED" => RdpSessionErrorKind.Cancelled,

            "FREERDP_ERROR_CONNECT_FAILED" or
            "FREERDP_ERROR_CONNECT_TRANSPORT_FAILED" or
            "FREERDP_ERROR_CONNECT_UNDEFINED" => RdpSessionErrorKind.TimeoutOrTransport,

            _ => RdpSessionErrorKind.Unknown
        };
    }

    private static string FriendlyMessageFor(string nativeName)
    {
        return KindFor(nativeName) switch
        {
            RdpSessionErrorKind.Dns => "DNS lookup failed.",
            RdpSessionErrorKind.Authentication => "Authentication failed.",
            RdpSessionErrorKind.AccessDenied => "Access denied.",
            RdpSessionErrorKind.CertificateOrTls => "TLS or certificate negotiation failed.",
            RdpSessionErrorKind.Cancelled => "Connection attempt was cancelled.",
            RdpSessionErrorKind.TimeoutOrTransport => "Connection transport failed or timed out.",
            _ => "Connection failed."
        };
    }
}

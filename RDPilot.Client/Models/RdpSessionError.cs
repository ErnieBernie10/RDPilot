namespace RDPilot.Client.Models;

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
            "WRAPPER_DIRECT_AND_GATEWAY_FAILED" => RdpSessionErrorKind.Gateway,

            "FREERDP_ERROR_DNS_ERROR" or
            "FREERDP_ERROR_DNS_NAME_NOT_FOUND" => RdpSessionErrorKind.Dns,

            "FREERDP_ERROR_AUTHENTICATION_FAILED" or
            "FREERDP_ERROR_CONNECT_LOGON_FAILURE" or
            "FREERDP_ERROR_CONNECT_WRONG_PASSWORD" or
            "FREERDP_ERROR_CONNECT_PASSWORD_EXPIRED" or
            "FREERDP_ERROR_CONNECT_PASSWORD_MUST_CHANGE" or
            "FREERDP_ERROR_CONNECT_PASSWORD_CERTAINLY_EXPIRED" or
            "FREERDP_ERROR_CONNECT_KDC_UNREACHABLE" or
            "FREERDP_ERROR_CONNECT_ACCOUNT_DISABLED" or
            "FREERDP_ERROR_CONNECT_ACCOUNT_RESTRICTION" or
            "FREERDP_ERROR_CONNECT_ACCOUNT_LOCKED_OUT" or
            "FREERDP_ERROR_CONNECT_ACCOUNT_EXPIRED" or
            "FREERDP_ERROR_CONNECT_LOGON_TYPE_NOT_GRANTED" or
            "FREERDP_ERROR_CONNECT_NO_OR_MISSING_CREDENTIALS" => RdpSessionErrorKind.Authentication,

            "FREERDP_ERROR_CONNECT_ACCESS_DENIED" or
            "FREERDP_ERROR_INSUFFICIENT_PRIVILEGES" or
            "FREERDP_ERROR_SERVER_DENIED_CONNECTION" or
            "FREERDP_ERROR_SERVER_INSUFFICIENT_PRIVILEGES" => RdpSessionErrorKind.AccessDenied,

            "FREERDP_ERROR_TLS_CONNECT_FAILED" or
            "FREERDP_ERROR_SECURITY_NEGO_CONNECT_FAILED" or
            "FREERDP_ERROR_CONNECT_CLIENT_REVOKED" => RdpSessionErrorKind.CertificateOrTls,

            "FREERDP_ERROR_CONNECT_CANCELLED" => RdpSessionErrorKind.Cancelled,

            "FREERDP_ERROR_CONNECT_ACTIVATION_TIMEOUT" or
            "FREERDP_ERROR_CONNECT_TARGET_BOOTING" or
            "FREERDP_ERROR_CONNECT_FAILED" or
            "FREERDP_ERROR_CONNECT_TRANSPORT_FAILED" or
            "FREERDP_ERROR_CONNECT_UNDEFINED" or
            "FREERDP_ERROR_PRE_CONNECT_FAILED" or
            "FREERDP_ERROR_POST_CONNECT_FAILED" or
            "FREERDP_ERROR_MCS_CONNECT_INITIAL_ERROR" => RdpSessionErrorKind.TimeoutOrTransport,

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
            RdpSessionErrorKind.Gateway => "Direct and gateway connection attempts both failed.",
            RdpSessionErrorKind.Cancelled => "Connection attempt was cancelled.",
            RdpSessionErrorKind.TimeoutOrTransport => "Connection transport failed or timed out.",
            _ => "Connection failed."
        };
    }
}

namespace RDPilot.Client.ViewModels;

/// <summary>
/// Managed mirror of the native <c>rdp_cursor_kind</c> enum in <c>freerdp_wrapper.h</c>.
/// The values must stay in sync; they cross the P/Invoke boundary as a plain int.
/// </summary>
internal enum RemoteCursorKind
{
    Hidden = 0,
    Default = 1,
    Bitmap = 2
}

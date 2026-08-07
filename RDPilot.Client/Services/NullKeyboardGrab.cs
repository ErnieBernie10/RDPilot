using System;

namespace RDPilot.Client.Services;

/// <summary>
/// Grab implementation for platforms with no keyboard capture support.
/// Wayland forbids client keyboard grabs outright, and the X11 XGrabKeyboard path
/// is not implemented yet, so Linux falls back to this no-op.
/// </summary>
internal sealed class NullKeyboardGrab : IKeyboardGrab
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "Keyboard grab is currently available on Windows only.";

    public bool IsEngaged => false;

    public event EventHandler<GrabbedKeyEventArgs>? KeyIntercepted
    {
        add { }
        remove { }
    }

    public void Attach(IntPtr windowHandle)
    {
    }

    public void SetEngaged(bool engaged)
    {
    }

    public void Dispose()
    {
    }
}

using System;

namespace RDPilot.Client.Services;

internal sealed class GrabbedKeyEventArgs : EventArgs
{
    public GrabbedKeyEventArgs(ushort scancode, bool extended, bool isUp)
    {
        Scancode = scancode;
        Extended = extended;
        IsUp = isUp;
    }

    public ushort Scancode { get; }
    public bool Extended { get; }
    public bool IsUp { get; }
}

/// <summary>
/// Routes the whole physical keyboard to the active RDP session, preventing the local
/// desktop from acting on shell chords such as the Windows key, Alt+Tab and Ctrl+Esc.
/// </summary>
internal interface IKeyboardGrab : IDisposable
{
    /// <summary>False when the current platform has no grab implementation.</summary>
    bool IsSupported { get; }

    /// <summary>Explains why grabbing is unavailable. Null when <see cref="IsSupported"/> is true.</summary>
    string? UnsupportedReason { get; }

    bool IsEngaged { get; }

    /// <summary>Raised for every intercepted key while engaged. The key is not delivered locally.</summary>
    event EventHandler<GrabbedKeyEventArgs>? KeyIntercepted;

    /// <summary>Associates the grab with the host window, used to scope interception to the foreground window.</summary>
    void Attach(IntPtr windowHandle);

    void SetEngaged(bool engaged);
}

internal static class KeyboardGrab
{
    public static IKeyboardGrab CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsKeyboardGrab();
        }

        return new NullKeyboardGrab();
    }
}

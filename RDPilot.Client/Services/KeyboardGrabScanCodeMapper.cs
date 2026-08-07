namespace RDPilot.Client.Services;

/// <summary>
/// Decodes a Windows low-level keyboard hook event into the raw RDP scancode form.
/// Pure logic so it can be tested without a hook or an Avalonia session.
/// </summary>
internal static class KeyboardGrabScanCodeMapper
{
    private const uint LowLevelKeyExtended = 0x01;
    private const uint LowLevelKeyUp = 0x80;

    private const uint VirtualKeyPause = 0x13;
    private const uint VirtualKeySnapshot = 0x2C;
    private const uint VirtualKeyNumLock = 0x90;

    private const ushort ScanCodeSnapshot = 0x37;
    private const ushort ScanCodePause = 0x45;
    private const ushort ScanCodeNumLock = 0x45;

    public static bool TryMapHookEvent(
        uint vkCode,
        uint scanCode,
        uint flags,
        out ushort scancode,
        out bool extended,
        out bool isUp)
    {
        isUp = (flags & LowLevelKeyUp) != 0;
        extended = (flags & LowLevelKeyExtended) != 0;
        scancode = (ushort)(scanCode & 0xFF);

        switch (vkCode)
        {
            case VirtualKeySnapshot:
                // PrintScreen reports an unreliable scancode; the wire form is extended 0x37.
                scancode = ScanCodeSnapshot;
                extended = true;
                break;
            case VirtualKeyPause:
                // Pause/Break needs the EXTENDED1 (0xE1) prefix, which the native input queue
                // cannot express. Send the bare scancode; Break is not distinguishable.
                scancode = ScanCodePause;
                extended = false;
                break;
            case VirtualKeyNumLock:
                scancode = ScanCodeNumLock;
                extended = false;
                break;
        }

        return scancode != 0;
    }
}

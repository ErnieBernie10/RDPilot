using Avalonia.Input;

namespace RDPilot.Client.Views;

internal static class RdpKeyboardInputMapper
{
    private const ushort KeyboardFlagsRelease = 0x8000;
    private const ushort KeyboardFlagsExtended = 0x0100;

    /// <summary>Marks a scancode in this table as an extended (0xE0-prefixed) key.</summary>
    public const ushort ExtendedScancodeBit = 0x100;

    public static bool TryMapKey(Key key, out ushort scancode)
    {
        scancode = key switch
        {
            Key.Escape => 0x01,
            Key.D1 => 0x02,
            Key.D2 => 0x03,
            Key.D3 => 0x04,
            Key.D4 => 0x05,
            Key.D5 => 0x06,
            Key.D6 => 0x07,
            Key.D7 => 0x08,
            Key.D8 => 0x09,
            Key.D9 => 0x0A,
            Key.D0 => 0x0B,
            Key.OemMinus => 0x0C,
            Key.OemPlus => 0x0D,
            Key.Back => 0x0E,
            Key.Tab => 0x0F,
            Key.Q => 0x10,
            Key.W => 0x11,
            Key.E => 0x12,
            Key.R => 0x13,
            Key.T => 0x14,
            Key.Y => 0x15,
            Key.U => 0x16,
            Key.I => 0x17,
            Key.O => 0x18,
            Key.P => 0x19,
            Key.OemOpenBrackets => 0x1A,
            Key.OemCloseBrackets => 0x1B,
            Key.Enter => 0x1C,
            Key.LeftCtrl => 0x1D,
            Key.A => 0x1E,
            Key.S => 0x1F,
            Key.D => 0x20,
            Key.F => 0x21,
            Key.G => 0x22,
            Key.H => 0x23,
            Key.J => 0x24,
            Key.K => 0x25,
            Key.L => 0x26,
            Key.OemSemicolon => 0x27,
            Key.OemQuotes => 0x28,
            Key.OemTilde => 0x29,
            Key.LeftShift => 0x2A,
            Key.OemBackslash => 0x2B,
            Key.OemPipe => 0x2B,
            Key.Z => 0x2C,
            Key.X => 0x2D,
            Key.C => 0x2E,
            Key.V => 0x2F,
            Key.B => 0x30,
            Key.N => 0x31,
            Key.M => 0x32,
            Key.OemComma => 0x33,
            Key.OemPeriod => 0x34,
            Key.OemQuestion => 0x35,
            Key.RightShift => 0x36,
            Key.Multiply => 0x37,
            Key.LeftAlt => 0x38,
            Key.Space => 0x39,
            Key.CapsLock => 0x3A,
            Key.F1 => 0x3B,
            Key.F2 => 0x3C,
            Key.F3 => 0x3D,
            Key.F4 => 0x3E,
            Key.F5 => 0x3F,
            Key.F6 => 0x40,
            Key.F7 => 0x41,
            Key.F8 => 0x42,
            Key.F9 => 0x43,
            Key.F10 => 0x44,
            Key.NumLock => 0x45,
            Key.Scroll => 0x46,
            Key.NumPad7 => 0x47,
            Key.NumPad8 => 0x48,
            Key.NumPad9 => 0x49,
            Key.Subtract => 0x4A,
            Key.NumPad4 => 0x4B,
            Key.NumPad5 => 0x4C,
            Key.NumPad6 => 0x4D,
            Key.Add => 0x4E,
            Key.NumPad1 => 0x4F,
            Key.NumPad2 => 0x50,
            Key.NumPad3 => 0x51,
            Key.NumPad0 => 0x52,
            Key.Decimal => 0x53,
            Key.F11 => 0x57,
            Key.F12 => 0x58,
            Key.Home => 0x147,
            Key.Up => 0x148,
            Key.PageUp => 0x149,
            Key.Divide => 0x135,
            Key.Left => 0x14B,
            Key.Right => 0x14D,
            Key.End => 0x14F,
            Key.Down => 0x150,
            Key.PageDown => 0x151,
            Key.Insert => 0x152,
            Key.Delete => 0x153,
            Key.RightCtrl => 0x11D,
            Key.RightAlt => 0x138,
            _ => 0
        };

        return scancode != 0;
    }

    public static ushort BuildKeyFlags(ushort scancode, bool isRelease, out ushort normalizedScancode)
    {
        ushort flags = isRelease ? KeyboardFlagsRelease : (ushort)0;
        normalizedScancode = scancode;
        if ((normalizedScancode & ExtendedScancodeBit) != 0)
        {
            flags |= KeyboardFlagsExtended;
            normalizedScancode &= 0xFF;
        }

        return flags;
    }
}

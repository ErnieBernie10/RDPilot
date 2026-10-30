using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RDPilot.Client;

[SuppressMessage("Interoperability", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "The wrapper uses explicit UTF-8/native interop signatures where needed.")]
[SuppressMessage("Performance", "CA1838:Avoid StringBuilder parameters for P/Invokes", Justification = "GetKeyboardLayoutName requires a writable character buffer.")]
internal static class NativeWrapper
{
    private const string LibName = "freerdp_wrapper";
    private const string NativeDllDirectoryEnvironmentVariable = "RDPILOT_NATIVE_DLL_DIR";

    static NativeWrapper()
    {
        ConfigureWindowsDllSearchPath();
    }

    private static void ConfigureWindowsDllSearchPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nativeDllDirectory = GetWindowsNativeDllDirectory();
        if (Directory.Exists(nativeDllDirectory))
        {
            SetDllDirectory(nativeDllDirectory);
        }
    }

    private static string GetWindowsNativeDllDirectory()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable(NativeDllDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return configuredDirectory;
        }

        return AppContext.BaseDirectory;
    }

    public static uint GetCurrentKeyboardLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var layoutName = new StringBuilder(9);
        if (GetKeyboardLayoutName(layoutName) &&
            uint.TryParse(layoutName.ToString(), System.Globalization.NumberStyles.HexNumber, null, out var parsedLayout))
        {
            return parsedLayout;
        }

        var keyboardLayout = GetKeyboardLayout(0);
        var rawLayout = (ulong)keyboardLayout.ToInt64();
        var lowWordLayout = (uint)(rawLayout & 0xFFFF);
        return lowWordLayout;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FrameCallback(IntPtr session, IntPtr data, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight, int sourceStride);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardTextCallback(IntPtr session, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardFilesCallback(IntPtr session, uint generation, IntPtr descriptor, nint descriptorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardFileContentsCallback(IntPtr session, uint streamId, [MarshalAs(UnmanagedType.I1)] bool ok, IntPtr data, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StatusCallback(IntPtr session, int status, uint errorCode, IntPtr errorName, IntPtr errorMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CursorCallback(IntPtr session, int kind, uint cursorId, int width, int height, int hotX, int hotY);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CertificateDecisionCallback(
        IntPtr session,
        IntPtr host,
        ushort port,
        IntPtr commonName,
        IntPtr subject,
        IntPtr issuer,
        IntPtr fingerprint,
        int isChanged,
        IntPtr previousSubject,
        IntPtr previousIssuer,
        IntPtr previousFingerprint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RdpSessionConfig
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Host;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? ConnectHost;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Domain;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? User;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Password;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? GatewayHost;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? GatewayDomain;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? GatewayUser;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? GatewayPassword;
        public ushort Port;
        public int Width;
        public int Height;
        public int ColorDepth;
        public uint ConnectionType;
        public uint KeyboardLayout;
        public uint DesktopScalePercent;
        public uint DeviceScalePercent;
        [MarshalAs(UnmanagedType.U1)] public bool NetworkAutoDetect;
        [MarshalAs(UnmanagedType.U1)] public bool UseNetworkLevelAuthentication;
        [MarshalAs(UnmanagedType.U1)] public bool Compression;
        [MarshalAs(UnmanagedType.U1)] public bool BitmapCache;
        [MarshalAs(UnmanagedType.U1)] public bool FontSmoothing;
        [MarshalAs(UnmanagedType.U1)] public bool DesktopWallpaper;
        [MarshalAs(UnmanagedType.U1)] public bool Themes;
        [MarshalAs(UnmanagedType.U1)] public bool MenuAnimations;
        [MarshalAs(UnmanagedType.U1)] public bool FullWindowDrag;
    }

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rdp_session_connect(
            in RdpSessionConfig config,
            FrameCallback frameCallback,
            ClipboardTextCallback clipboardCallback,
            ClipboardFilesCallback clipboardFilesCallback,
            StatusCallback statusCallback,
            CertificateDecisionCallback certificateDecisionCallback,
            CursorCallback cursorCallback,
            ClipboardFileContentsCallback clipboardFileContentsCallback);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_disconnect(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_free(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_update_resolution(IntPtr session, int width, int height);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_send_mouse_event(IntPtr session, ushort flags, ushort x, ushort y);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_send_keyboard_event(IntPtr session, ushort flags, ushort code);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_clipboard_set_local_text(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_clipboard_clear_local_files(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_clipboard_add_local_file(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_clipboard_commit_local_files(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool rdp_session_clipboard_request_remote_file_contents(IntPtr session, uint generation, uint streamId,
        uint listIndex, [MarshalAs(UnmanagedType.I1)] bool sizeOnly, ulong offset, uint size);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_clipboard_set_local_bitmap(IntPtr session, IntPtr bitmapData, nint bitmapDataSize, uint width, uint height);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_request_full_frame(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool rdp_session_present(
        IntPtr session,
        IntPtr dest,
        int destStride,
        int destWidth,
        int destHeight,
        out int dirtyX,
        out int dirtyY,
        out int dirtyWidth,
        out int dirtyHeight,
        out int fbWidth,
        out int fbHeight);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool rdp_session_copy_cursor_image(
        IntPtr session,
        uint cursorId,
        IntPtr dest,
        int destStride,
        int destWidth,
        int destHeight);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetKeyboardLayoutName(StringBuilder pwszKLID);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

}

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
            Console.WriteLine($"[DEBUG] Native DLL directory: '{nativeDllDirectory}'");
            ConfigureOpenSslProviderPath(nativeDllDirectory);
            SetDllDirectory(nativeDllDirectory);
        }
    }

    private static void ConfigureOpenSslProviderPath(string nativeDllDirectory)
    {
        var legacyProviderPath = Path.Combine(nativeDllDirectory, "legacy.dll");
        if (!File.Exists(legacyProviderPath))
        {
            return;
        }

        var openSslConfigPath = Path.Combine(nativeDllDirectory, "openssl-rdpilot.cnf");
        if (File.Exists(openSslConfigPath))
        {
            Environment.SetEnvironmentVariable("OPENSSL_CONF", openSslConfigPath);
        }

        Environment.SetEnvironmentVariable("OPENSSL_MODULES", nativeDllDirectory);
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

    public static string ResolveDirectConnectHost(string host)
    {
        if (!OperatingSystem.IsWindows())
        {
            return host;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                ?? addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6)?.ToString()
                ?? host;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[DEBUG] Windows DNS pre-resolution failed for '{host}': {ex.Message}");
            return host;
        }
    }

    public static uint GetCurrentKeyboardLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("[KEYBOARD] platform=non-windows layout=0x00000000 source=default");
            return 0;
        }

        var layoutName = new StringBuilder(9);
        if (GetKeyboardLayoutName(layoutName) &&
            uint.TryParse(layoutName.ToString(), System.Globalization.NumberStyles.HexNumber, null, out var parsedLayout))
        {
            Console.WriteLine($"[KEYBOARD] platform=windows layout=0x{parsedLayout:X8} source=GetKeyboardLayoutName name={layoutName}");
            return parsedLayout;
        }

        var keyboardLayout = GetKeyboardLayout(0);
        var rawLayout = (ulong)keyboardLayout.ToInt64();
        var lowWordLayout = (uint)(rawLayout & 0xFFFF);
        Console.WriteLine($"[KEYBOARD] platform=windows layout=0x{lowWordLayout:X8} rawHkl=0x{rawLayout:X16} source=GetKeyboardLayout fallbackLastWin32Error={Marshal.GetLastWin32Error()}");
        return lowWordLayout;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FrameCallback(IntPtr session, IntPtr data, int width, int height, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight, int sourceStride);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardTextCallback(IntPtr session, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardFilesCallback(IntPtr session, IntPtr filePaths, nint fileCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StatusCallback(IntPtr session, int status, uint errorCode, IntPtr errorName, IntPtr errorMessage);

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

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rdp_session_connect(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string connectHost,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string domain,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? gatewayHost,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? gatewayDomain,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? gatewayUser,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? gatewayPassword,
            int width, int height,
            int colorDepth,
            [MarshalAs(UnmanagedType.I1)] bool compression,
            [MarshalAs(UnmanagedType.I1)] bool fontSmoothing,
            [MarshalAs(UnmanagedType.I1)] bool bitmapCache,
            [MarshalAs(UnmanagedType.I1)] bool desktopWallpaper,
            [MarshalAs(UnmanagedType.I1)] bool themes,
            [MarshalAs(UnmanagedType.I1)] bool menuAnimations,
            [MarshalAs(UnmanagedType.I1)] bool fullWindowDrag,
            int connectionType,
            uint keyboardLayout,
            uint dpiScalePercent,
            FrameCallback frameCallback,
            ClipboardTextCallback clipboardCallback,
            ClipboardFilesCallback clipboardFilesCallback,
            StatusCallback statusCallback,
            CertificateDecisionCallback certificateDecisionCallback);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_disconnect(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_free(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rdp_session_update_resolution(IntPtr session, int width, int height, uint dpiScalePercent);

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
    internal static extern void rdp_session_clipboard_set_local_bitmap(IntPtr session, IntPtr bitmapData, nint bitmapDataSize, uint width, uint height);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetKeyboardLayoutName(StringBuilder pwszKLID);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RDPilot.Client;

public static class NativeWrapper
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
        if (!File.Exists(Path.Combine(nativeDllDirectory, "legacy.dll")))
        {
            return;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrameCallback(IntPtr session, IntPtr data, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ClipboardTextCallback(IntPtr session, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StatusCallback(IntPtr session, int status, uint errorCode, IntPtr errorName, IntPtr errorMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CertificateDecisionCallback(
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
    public static extern IntPtr rdp_session_connect(
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
            FrameCallback frameCallback,
            ClipboardTextCallback clipboardCallback,
            StatusCallback statusCallback,
            CertificateDecisionCallback certificateDecisionCallback);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_disconnect(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_free(IntPtr session);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_update_resolution(IntPtr session, int width, int height);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_send_mouse_event(IntPtr session, ushort flags, ushort x, ushort y);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_send_keyboard_event(IntPtr session, ushort flags, ushort code);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rdp_session_clipboard_set_local_text(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

}

using System;
using System.Runtime.InteropServices;

namespace RDP.Client;

public static class NativeWrapper
{
    private const string LibName = "freerdp_wrapper";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrameCallback(IntPtr data, int width, int height);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void set_frame_callback(FrameCallback cb);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool connect_rdp(
            string host, string domain, string user, string password,
            string? gatewayHost, string? gatewayDomain, string? gatewayUser, string? gatewayPassword,
            int width, int height);


    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void disconnect_rdp();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void update_resolution(int width, int height);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void send_mouse_event(ushort flags, ushort x, ushort y);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void send_keyboard_event(ushort flags, ushort code);
}

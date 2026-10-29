using System;
using System.Net;
using System.Runtime.InteropServices;
using RDPilot.Client.Models;

namespace RDPilot.Client;

internal interface INativeRdpSession
{
    IntPtr Handle { get; }

    void Disconnect();
    void Free();
    void UpdateResolution(int width, int height);
    void SendMouseEvent(ushort flags, ushort x, ushort y);
    void SendKeyboardEvent(ushort flags, ushort code);
    void SetLocalClipboardText(string? text);
    void SetLocalClipboardFiles(string[] filePaths);
    void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height);
    bool RequestRemoteFileContents(uint generation, uint streamId, uint listIndex, bool sizeOnly, ulong offset, uint size);
    void RequestFullFrame();
    FramePresentResult Present(IntPtr dest, int destStride, int destWidth, int destHeight);
    bool CopyCursorImage(uint cursorId, IntPtr dest, int destStride, int destWidth, int destHeight);
}

internal sealed class NativeRdpSession : INativeRdpSession
{
    private NativeRdpSession(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }

    private static readonly RdpConnectEnvironment PlatformEnvironment =
        new(OperatingSystem.IsWindows(), Dns.GetHostAddresses, NativeWrapper.GetCurrentKeyboardLayout);

    public static INativeRdpSession Connect(RdpSessionStartRequest request, NativeSessionCallbacks callbacks)
    {
        var plan = request.CreateConnectPlan(PlatformEnvironment);
        var config = new NativeWrapper.RdpSessionConfig
        {
            Size = (uint)Marshal.SizeOf<NativeWrapper.RdpSessionConfig>(),
            Host = plan.Host,
            ConnectHost = plan.ConnectHost,
            Domain = plan.Domain,
            User = plan.Username,
            Password = plan.Password,
            GatewayHost = plan.GatewayHost,
            GatewayDomain = plan.GatewayDomain,
            GatewayUser = plan.GatewayUsername,
            GatewayPassword = plan.GatewayPassword,
            Port = plan.Port,
            Width = plan.Width,
            Height = plan.Height,
            ColorDepth = plan.ColorDepth,
            ConnectionType = plan.ConnectionType,
            KeyboardLayout = plan.KeyboardLayout,
            DesktopScalePercent = plan.DesktopScalePercent,
            DeviceScalePercent = plan.DeviceScalePercent,
            NetworkAutoDetect = plan.NetworkAutoDetect,
            UseNetworkLevelAuthentication = plan.UseNetworkLevelAuthentication,
            Compression = plan.Compression,
            BitmapCache = plan.BitmapCache,
            FontSmoothing = plan.FontSmoothing,
            DesktopWallpaper = plan.DesktopWallpaper,
            Themes = plan.Themes,
            MenuAnimations = plan.MenuAnimations,
            FullWindowDrag = plan.FullWindowDrag
        };
        var handle = NativeWrapper.rdp_session_connect(
            in config,
            callbacks.Frame,
            callbacks.ClipboardText,
            callbacks.ClipboardFiles,
            callbacks.Status,
            callbacks.Certificate,
            callbacks.Cursor,
            callbacks.ClipboardFileContents);

        return new NativeRdpSession(handle);
    }

    public void Disconnect() => NativeWrapper.rdp_session_disconnect(Handle);

    public void Free() => NativeWrapper.rdp_session_free(Handle);

    public void UpdateResolution(int width, int height) =>
        NativeWrapper.rdp_session_update_resolution(Handle, width, height);

    public void SendMouseEvent(ushort flags, ushort x, ushort y) =>
        NativeWrapper.rdp_session_send_mouse_event(Handle, flags, x, y);

    public void SendKeyboardEvent(ushort flags, ushort code) =>
        NativeWrapper.rdp_session_send_keyboard_event(Handle, flags, code);

    public void SetLocalClipboardText(string? text) =>
        NativeWrapper.rdp_session_clipboard_set_local_text(Handle, text);

    public void SetLocalClipboardFiles(string[] filePaths)
    {
        NativeWrapper.rdp_session_clipboard_clear_local_files(Handle);
        foreach (var filePath in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                NativeWrapper.rdp_session_clipboard_add_local_file(Handle, filePath);
            }
        }

        NativeWrapper.rdp_session_clipboard_commit_local_files(Handle);
    }

    public void SetLocalClipboardBitmap(byte[] bitmapData, uint width, uint height)
    {
        var bitmapHandle = GCHandle.Alloc(bitmapData, GCHandleType.Pinned);
        try
        {
            NativeWrapper.rdp_session_clipboard_set_local_bitmap(Handle, bitmapHandle.AddrOfPinnedObject(), bitmapData.Length, width, height);
        }
        finally
        {
            bitmapHandle.Free();
        }
    }

    public bool RequestRemoteFileContents(uint generation, uint streamId, uint listIndex, bool sizeOnly, ulong offset, uint size) =>
        NativeWrapper.rdp_session_clipboard_request_remote_file_contents(Handle, generation, streamId, listIndex, sizeOnly, offset, size);

    public void RequestFullFrame() => NativeWrapper.rdp_session_request_full_frame(Handle);

    public FramePresentResult Present(IntPtr dest, int destStride, int destWidth, int destHeight)
    {
        var presented = NativeWrapper.rdp_session_present(Handle, dest, destStride, destWidth, destHeight,
            out var dirtyX, out var dirtyY, out var dirtyWidth, out var dirtyHeight, out var fbWidth, out var fbHeight);
        return new FramePresentResult(presented, dirtyX, dirtyY, dirtyWidth, dirtyHeight, fbWidth, fbHeight);
    }

    public bool CopyCursorImage(uint cursorId, IntPtr dest, int destStride, int destWidth, int destHeight) =>
        NativeWrapper.rdp_session_copy_cursor_image(Handle, cursorId, dest, destStride, destWidth, destHeight);
}

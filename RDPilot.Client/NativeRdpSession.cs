using System;

namespace RDPilot.Client;

internal interface INativeRdpSession
{
    IntPtr Handle { get; }

    void Disconnect();
    void Free();
    void UpdateResolution(int width, int height, uint dpiScalePercent);
    void SendMouseEvent(ushort flags, ushort x, ushort y);
    void SendKeyboardEvent(ushort flags, ushort code);
    void SetLocalClipboardText(string? text);
    void SetLocalClipboardFiles(string[] filePaths);
    void SetLocalClipboardBitmap(IntPtr bitmapData, nint bitmapDataSize, uint width, uint height);
    bool Present(
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
}

internal sealed class NativeRdpSession : INativeRdpSession
{
    private NativeRdpSession(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }

    public static INativeRdpSession Connect(
        string host,
        string connectHost,
        string domain,
        string user,
        string password,
        string? gatewayHost,
        string? gatewayDomain,
        string? gatewayUser,
        string? gatewayPassword,
        int width,
        int height,
        int colorDepth,
        bool compression,
        bool fontSmoothing,
        bool bitmapCache,
        bool desktopWallpaper,
        bool themes,
        bool menuAnimations,
        bool fullWindowDrag,
        int connectionType,
        bool networkAutoDetect,
        uint keyboardLayout,
        uint dpiScalePercent,
        NativeWrapper.FrameCallback frameCallback,
        NativeWrapper.ClipboardTextCallback clipboardCallback,
        NativeWrapper.ClipboardFilesCallback clipboardFilesCallback,
        NativeWrapper.StatusCallback statusCallback,
        NativeWrapper.CertificateDecisionCallback certificateDecisionCallback)
    {
        var handle = NativeWrapper.rdp_session_connect(
            host,
            connectHost,
            domain,
            user,
            password,
            gatewayHost,
            gatewayDomain,
            gatewayUser,
            gatewayPassword,
            width,
            height,
            colorDepth,
            compression,
            fontSmoothing,
            bitmapCache,
            desktopWallpaper,
            themes,
            menuAnimations,
            fullWindowDrag,
            connectionType,
            networkAutoDetect,
            keyboardLayout,
            dpiScalePercent,
            frameCallback,
            clipboardCallback,
            clipboardFilesCallback,
            statusCallback,
            certificateDecisionCallback);

        return new NativeRdpSession(handle);
    }

    public void Disconnect() => NativeWrapper.rdp_session_disconnect(Handle);

    public void Free() => NativeWrapper.rdp_session_free(Handle);

    public void UpdateResolution(int width, int height, uint dpiScalePercent) =>
        NativeWrapper.rdp_session_update_resolution(Handle, width, height, dpiScalePercent);

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

    public void SetLocalClipboardBitmap(IntPtr bitmapData, nint bitmapDataSize, uint width, uint height) =>
        NativeWrapper.rdp_session_clipboard_set_local_bitmap(Handle, bitmapData, bitmapDataSize, width, height);

    public bool Present(
        IntPtr dest,
        int destStride,
        int destWidth,
        int destHeight,
        out int dirtyX,
        out int dirtyY,
        out int dirtyWidth,
        out int dirtyHeight,
        out int fbWidth,
        out int fbHeight) =>
        NativeWrapper.rdp_session_present(Handle, dest, destStride, destWidth, destHeight, out dirtyX, out dirtyY, out dirtyWidth, out dirtyHeight, out fbWidth, out fbHeight);
}

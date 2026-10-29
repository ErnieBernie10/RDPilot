using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Tests;

/// <summary>Exercises real session construction and callbacks through an injected native connector.</summary>
internal sealed class SessionTestContext : IDisposable
{
    internal SessionTestContext(string name = "Test", double scaling = 1,
        Func<RdpCertificatePrompt, CertificateTrustDecision>? decideCertificate = null,
        Action<NativeSessionCallbacks>? duringConnect = null, bool start = true, IntPtr? handle = null,
        SavedConnection? connection = null)
    {
        Request = new RdpSessionStartRequest(connection ?? new SavedConnection { Name = name, Host = "test.example", Username = "user" },
            "", "", 200, 200, scaling,
            new RdpEffectiveQualitySettings(16, false, false, false, false, false, true, true, RdpConnectionType.Wan));
        Native = new RecordingNativeSession(handle ?? new IntPtr(0x1234));
        Runtime = new RdpSessionRuntime(Request.Connection.Host, decideCertificate ?? (_ => CertificateTrustDecision.Reject),
            (_, callbacks) =>
            {
                Callbacks = callbacks;
                duringConnect?.Invoke(callbacks);
                return Native;
            });
        ViewModel =AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var viewModel = new RdpSessionViewModel(Request, Runtime);
            viewModel.RemoteClipboardTextReceived += (_, text) => ClipboardTexts.Add(text);
            viewModel.RemoteClipboardFilesReceived += (_, offer) => ClipboardFiles.Add(offer);
            return viewModel;
        });
        if (start) AvaloniaTestEnvironment.RunOnUiThread(() => ViewModel.Start(Request));
    }

    internal RdpSessionStartRequest Request { get; }
    internal RdpSessionViewModel ViewModel { get; }
    internal RdpSessionRuntime Runtime { get; }
    internal RecordingNativeSession Native { get; }
    internal NativeSessionCallbacks? Callbacks { get; private set; }
    internal List<string> ClipboardTexts { get; } = [];
    internal List<RemoteFileOffer> ClipboardFiles { get; } = [];

    internal static RdpSessionViewModel CreateSession(SavedConnection connection, RdpSessionStatus status)
    {
        var context = new SessionTestContext(connection: connection);
        switch (status)
        {
            case RdpSessionStatus.Connecting:
                break;
            case RdpSessionStatus.Connected:
                context.ConnectStatus();
                break;
            case RdpSessionStatus.Disconnected:
                context.DisconnectStatus();
                break;
            case RdpSessionStatus.Failed:
                context.FailStatus(1, "FREERDP_ERROR_CONNECT_FAILED", "Connection failed.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
        return context.ViewModel;
    }

    internal void ConnectStatus() => RaiseStatus(1);

    internal void DisconnectStatus() => RaiseStatus(3);

    internal void FailStatus(uint code, string name, string message)
    {
        var namePointer = Marshal.StringToCoTaskMemUTF8(name);
        var messagePointer = Marshal.StringToCoTaskMemUTF8(message);
        try { RaiseStatus(2, code, namePointer, messagePointer); }
        finally { Marshal.FreeCoTaskMem(namePointer); Marshal.FreeCoTaskMem(messagePointer); }
    }

    private void RaiseStatus(int status, uint code = 0, IntPtr name = default, IntPtr message = default) =>
        AvaloniaTestEnvironment.RunOnUiThread(() => Callbacks!.Status(Native.Handle, status, code, name, message));

    public void Dispose() => AvaloniaTestEnvironment.RunOnUiThread(ViewModel.Dispose);
}

internal sealed class RecordingNativeSession(IntPtr handle) : INativeRdpSession
{
    public IntPtr Handle { get; } = handle;
    internal int DisconnectCount { get; private set; }
    internal int FreeCount { get; private set; }
    internal int FullFrameCount { get; private set; }
    internal List<(int Width, int Height, uint Dpi)> ResolutionUpdates { get; } = [];
    internal List<(ushort Flags, ushort X, ushort Y)> MouseEvents { get; } = [];
    internal List<(ushort Flags, ushort Code)> KeyboardEvents { get; } = [];
    internal List<string> ClipboardTexts { get; } = [];
    internal List<string[]> ClipboardFiles { get; } = [];
    internal List<(byte[] Data, uint Width, uint Height)> ClipboardBitmaps { get; } = [];
    internal List<uint> CursorRequests { get; } = [];
    internal List<(uint Generation, uint StreamId, uint ListIndex, bool SizeOnly, ulong Offset, uint Size)> FileContentsRequests { get; } = [];
    internal bool AcceptFileContentsRequests { get; set; } = true;

    public void Disconnect() => DisconnectCount++;
    public void Free() => FreeCount++;
    public void UpdateResolution(int width, int height, uint dpiScalePercent) => ResolutionUpdates.Add((width, height, dpiScalePercent));
    public void SendMouseEvent(ushort flags, ushort x, ushort y) => MouseEvents.Add((flags, x, y));
    public void SendKeyboardEvent(ushort flags, ushort code) => KeyboardEvents.Add((flags, code));
    public void SetLocalClipboardText(string? text) => ClipboardTexts.Add(text ?? "");
    public void SetLocalClipboardFiles(string[] paths) => ClipboardFiles.Add((string[])paths.Clone());
    public void SetLocalClipboardBitmap(byte[] data, uint width, uint height) => ClipboardBitmaps.Add(((byte[])data.Clone(), width, height));
    public bool RequestRemoteFileContents(uint generation, uint streamId, uint listIndex, bool sizeOnly, ulong offset, uint size)
    {
        if (!AcceptFileContentsRequests) return false;
        FileContentsRequests.Add((generation, streamId, listIndex, sizeOnly, offset, size));
        return true;
    }
    public void RequestFullFrame() => FullFrameCount++;
    public FramePresentResult Present(IntPtr dest, int stride, int width, int height) => default;
    public bool CopyCursorImage(uint cursorId, IntPtr dest, int stride, int width, int height)
    {
        CursorRequests.Add(cursorId);
        return false;
    }
}

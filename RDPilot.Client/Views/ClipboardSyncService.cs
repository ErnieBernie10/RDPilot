using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;

namespace RDPilot.Client.Views;

[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Stateful service by design.")]
internal sealed class ClipboardSyncService
{
    private string? _lastClipboardSignature;
    private object? _activeSession;
    private int _remoteUpdatesInProgress;
    private int _emptyReads;

    public bool ShouldPollLocalClipboard => _remoteUpdatesInProgress == 0;

    public void UseSession(object? session)
    {
        if (ReferenceEquals(_activeSession, session)) return;
        _activeSession = session;
        ClearSignature();
    }

    public void BeginRemoteTextUpdate(string text)
    {
        _remoteUpdatesInProgress++;
        _lastClipboardSignature = BuildTextSignature(text);
        _emptyReads = 0;
    }

    public void BeginRemoteFilesUpdate(string[] filePaths)
    {
        _remoteUpdatesInProgress++;
        _lastClipboardSignature = BuildFilesSignature(filePaths);
        _emptyReads = 0;
    }

    public void EndRemoteUpdate()
    {
        if (_remoteUpdatesInProgress > 0) _remoteUpdatesInProgress--;
    }

    public bool ClearSignature()
    {
        _emptyReads = 0;
        if (_lastClipboardSignature == null)
        {
            return false;
        }

        _lastClipboardSignature = null;
        return true;
    }

    public bool ObserveEmptyClipboard()
    {
        // Clipboard reads can briefly return empty while another application owns it.
        if (++_emptyReads < 2)
        {
            return false;
        }

        return ClearSignature();
    }

    public bool TryRememberText(string text, out string signature)
    {
        signature = BuildTextSignature(text);
        return TryRememberSignature(signature);
    }

    public bool TryRememberFiles(string[] filePaths, out string signature)
    {
        signature = BuildFilesSignature(filePaths);
        return TryRememberSignature(signature);
    }

    public bool TryRememberBitmap(Bitmap bitmap, out string signature)
    {
        signature = $"bitmap:{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}:{bitmap.Format}:{bitmap.AlphaFormat}";
        return TryRememberSignature(signature);
    }

    private static string BuildTextSignature(string text) => $"text:{text}";
    private static string BuildFilesSignature(string[] filePaths) => $"files:{string.Join("\n", filePaths)}";

    private bool TryRememberSignature(string signature)
    {
        _emptyReads = 0;
        if (signature == _lastClipboardSignature)
        {
            return false;
        }

        _lastClipboardSignature = signature;
        return true;
    }
}

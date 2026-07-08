using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;

namespace RDPilot.Client.Views;

[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Stateful service by design.")]
internal sealed class ClipboardSyncService
{
    private string? _lastClipboardSignature;
    private bool _settingClipboardFromRemote;

    public bool ShouldPollLocalClipboard => !_settingClipboardFromRemote;

    public void BeginRemoteTextUpdate(string text)
    {
        _settingClipboardFromRemote = true;
        _lastClipboardSignature = BuildTextSignature(text);
    }

    public void BeginRemoteFilesUpdate(string[] filePaths)
    {
        _settingClipboardFromRemote = true;
        _lastClipboardSignature = BuildFilesSignature(filePaths);
    }

    public void EndRemoteTextUpdate()
    {
        _settingClipboardFromRemote = false;
    }

    public bool ClearSignature()
    {
        if (_lastClipboardSignature == null)
        {
            return false;
        }

        _lastClipboardSignature = null;
        return true;
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
        if (signature == _lastClipboardSignature)
        {
            return false;
        }

        _lastClipboardSignature = signature;
        return true;
    }
}

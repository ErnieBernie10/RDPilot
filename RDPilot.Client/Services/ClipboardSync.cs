using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Services;

/// <summary>Synchronizes the selected session with the host clipboard and owns its polling lifetime.</summary>
internal sealed class ClipboardSync : IDisposable
{
    private readonly Func<IClipboard?> _getClipboard;
    private readonly Func<string[], Task<IStorageItem[]>> _createStorageItems;
    private readonly Func<Bitmap, byte[]?> _convertBitmapToDib;
    private readonly IRemoteFileClipboard? _remoteFiles;
    private readonly Func<RemoteFileOffer, CancellationToken, Task<string[]>> _downloadFiles;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ISelectedSessionSource? _source;
    private RdpSessionViewModel? _session;
    private CancellationTokenSource? _download;
    private string? _lastSignature;
    private int _remoteUpdatesInProgress;
    private int _emptyReads;
    private bool _polling;
    private bool _readErrorReported;
    private string? _lastInvalidFilePath;
    private int _generation;
    private bool _attached;
    private bool _disposed;

    internal ClipboardSync(Func<IClipboard?> getClipboard, Func<string[], Task<IStorageItem[]>> createStorageItems,
        Func<Bitmap, byte[]?> convertBitmapToDib, IRemoteFileClipboard? remoteFiles = null,
        Func<RemoteFileOffer, CancellationToken, Task<string[]>>? downloadFiles = null)
    {
        _getClipboard = getClipboard;
        _createStorageItems = createStorageItems;
        _convertBitmapToDib = convertBitmapToDib;
        _remoteFiles = remoteFiles;
        _downloadFiles = downloadFiles ?? ((offer, token) => RemoteFileDownloader.DownloadAsync(offer, AppDataPaths.ClipboardCacheDirectory, token));
        _pollTimer.Tick += OnPollTimerTick;
    }

    private bool ShouldPollLocalClipboard => _remoteUpdatesInProgress == 0;

    internal void UseSource(ISelectedSessionSource? source)
    {
        if (!ReferenceEquals(source, _source))
        {
            if (_source != null) _source.SelectedSessionChanged -= OnSelectedSessionChanged;
            _source = source;
            if (source != null) source.SelectedSessionChanged += OnSelectedSessionChanged;
        }
        SelectionChanged();
    }

    internal void Start()
    {
        if (_disposed) return;
        _attached = true;
        SelectionChanged();
        _pollTimer.Start();
    }

    internal void Stop()
    {
        _attached = false;
        SelectionChanged();
        _pollTimer.Stop();
    }

    private void OnSelectedSessionChanged(object? sender, EventArgs e) => SelectionChanged();

    private void SelectionChanged()
    {
        _generation++;
        CancelDownload();
        var session = _attached && !_disposed ? _source?.SelectedSession : null;
        if (ReferenceEquals(session, _session)) return;
        if (_session != null)
        {
            _session.RemoteClipboardTextReceived -= OnRemoteTextReceived;
            _session.RemoteClipboardFilesReceived -= OnRemoteFilesReceived;
        }
        _session = session;
        if (session != null)
        {
            session.RemoteClipboardTextReceived += OnRemoteTextReceived;
            session.RemoteClipboardFilesReceived += OnRemoteFilesReceived;
        }
        ClearSignature();
    }

    private void OnRemoteTextReceived(object? sender, string text)
    {
        if (sender is RdpSessionViewModel session) Dispatcher.UIThread.Post(() => _ = HandleRemoteTextAsync(session, text));
    }

    private void OnRemoteFilesReceived(object? sender, RemoteFileOffer offer)
    {
        if (sender is RdpSessionViewModel session) Dispatcher.UIThread.Post(() => _ = HandleRemoteFileOfferAsync(session, offer));
    }

    private void OnPollTimerTick(object? sender, EventArgs e) => _ = PollClipboardAsync();

    private bool IsCurrent(RdpSessionViewModel session, int generation) =>
        generation == _generation && ReferenceEquals(session, _session);

    internal async Task HandleRemoteTextAsync(RdpSessionViewModel session, string text)
    {
        if (!IsCurrent(session, _generation) || _getClipboard() is not { } clipboard) return;
        CancelDownload();
        try
        {
            _generation++;
            BeginRemoteUpdate(TextSignature(text));
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard text: {0}", ex);
        }
        finally
        {
            EndRemoteUpdate();
        }
    }

    internal async Task HandleRemoteFileOfferAsync(RdpSessionViewModel session, RemoteFileOffer offer)
    {
        if (!IsCurrent(session, _generation)) return;
        CancelDownload();
        _generation++;
        var generation = _generation;
        if (_remoteFiles != null)
        {
            BeginRemoteUpdate(null);
            try
            {
                if (await _remoteFiles.OfferAsync(offer)) return;
            }
            finally
            {
                EndRemoteUpdate();
            }
            Trace.TraceWarning("Unable to offer remote clipboard files as virtual files; downloading them instead.");
            if (!IsCurrent(session, generation)) return;
        }

        var download = new CancellationTokenSource();
        _download = download;
        string[] paths;
        try
        {
            paths = await _downloadFiles(offer, download.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!download.IsCancellationRequested) Trace.TraceWarning("Unable to download remote clipboard files: {0}", ex.Message);
            return;
        }
        finally
        {
            if (ReferenceEquals(_download, download)) _download = null;
            download.Dispose();
        }

        if (IsCurrent(session, generation)) await PublishRemoteFilesAsync(session, paths);
    }

    private void CancelDownload()
    {
        var download = _download;
        _download = null;
        download?.Cancel();
    }

    private async Task PublishRemoteFilesAsync(RdpSessionViewModel session, string[] paths)
    {
        if (paths.Length == 0 || _getClipboard() is not { } clipboard) return;
        IStorageItem[]? items = null;
        try
        {
            _generation++;
            var generation = _generation;
            BeginRemoteUpdate(FilesSignature(paths));
            items = await _createStorageItems(paths);
            if (!IsCurrent(session, generation))
            {
                foreach (var item in items) item.Dispose();
                return;
            }
            if (items.Length == 0) throw new InvalidOperationException("No local clipboard files could be resolved.");
            await clipboard.SetFilesAsync(items);
        }
        catch (Exception ex)
        {
            if (items != null) foreach (var item in items) item.Dispose();
            ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard files: {0}", ex);
        }
        finally
        {
            EndRemoteUpdate();
        }
    }

    internal async Task PollClipboardAsync()
    {
        var session = _session;
        if (session?.IsConnected != true)
        {
            ClearSignature();
            return;
        }

        var clipboard = _getClipboard();
        if (_polling || !ShouldPollLocalClipboard || clipboard == null || _remoteFiles?.OwnsClipboard == true) return;

        _polling = true;
        var generation = _generation;
        try
        {
            var data = await clipboard.TryGetDataAsync();
            if (!IsCurrent(session, generation) || !ShouldPollLocalClipboard) return;
            if (data == null)
            {
                _readErrorReported = false;
                // Announcing an empty clipboard takes ownership away from the server and fails a download in progress.
                if (_download == null && ObserveEmptyClipboard()) session.SetLocalClipboardText("");
                return;
            }

            var files = await data.TryGetFilesAsync();
            if (!IsCurrent(session, generation) || !ShouldPollLocalClipboard)
            {
                if (files != null) foreach (var item in files) item.Dispose();
                return;
            }
            if (files is { Length: > 0 })
            {
                var paths = new List<string>(files.Length);
                foreach (var item in files)
                {
                    using (item)
                    {
                        var path = item.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            _lastInvalidFilePath = null;
                            paths.Add(path);
                        }
                        else if ((path ?? "<no local path>") != _lastInvalidFilePath)
                        {
                            Trace.TraceWarning("Clipboard file has no readable local path (folders are not supported): {0}", path);
                            _lastInvalidFilePath = path ?? "<no local path>";
                        }
                    }
                }

                if (paths.Count > 0)
                {
                    _readErrorReported = false;
                    if (Remember(FilesSignature(paths.ToArray())))
                    {
                        CancelDownload();
                        session.SetLocalClipboardFiles(paths.ToArray());
                    }
                    return;
                }
            }

            var text = await data.TryGetTextAsync();
            if (!IsCurrent(session, generation) || !ShouldPollLocalClipboard) return;
            if (!string.IsNullOrEmpty(text))
            {
                _readErrorReported = false;
                if (Remember(TextSignature(text)))
                {
                    CancelDownload();
                    session.SetLocalClipboardText(text);
                }
                return;
            }

            var bitmap = await data.TryGetBitmapAsync();
            if (!IsCurrent(session, generation) || !ShouldPollLocalClipboard) return;
            _readErrorReported = false;
            if (bitmap != null)
            {
                var dib = _convertBitmapToDib(bitmap);
                if (dib != null && Remember(BitmapSignature(dib)))
                {
                    CancelDownload();
                    session.SetLocalClipboardBitmap(dib, (uint)bitmap.PixelSize.Width, (uint)bitmap.PixelSize.Height);
                }
                return;
            }

            if (bitmap == null && _download == null && ObserveEmptyClipboard()) session.SetLocalClipboardText("");
        }
        catch (Exception ex)
        {
            if (!_readErrorReported)
            {
                Trace.TraceWarning("Unable to read local clipboard: {0}", ex);
                _readErrorReported = true;
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private void BeginRemoteUpdate(string? signature)
    {
        _remoteUpdatesInProgress++;
        _lastSignature = signature;
        _emptyReads = 0;
    }

    private void EndRemoteUpdate()
    {
        if (_remoteUpdatesInProgress > 0) _remoteUpdatesInProgress--;
    }

    private bool ClearSignature()
    {
        _emptyReads = 0;
        if (_lastSignature == null) return false;
        _lastSignature = null;
        return true;
    }

    private bool ObserveEmptyClipboard()
    {
        // Clipboard reads can briefly return empty while another application owns it.
        if (++_emptyReads < 2) return false;
        return ClearSignature();
    }

    private bool Remember(string signature)
    {
        _emptyReads = 0;
        if (signature == _lastSignature) return false;
        _lastSignature = signature;
        return true;
    }

    private static string TextSignature(string text) => $"text:{text}";
    private static string FilesSignature(string[] filePaths) => $"files:{string.Join("\n", filePaths)}";
    private static string BitmapSignature(byte[] dib) => $"bitmap:{Convert.ToHexString(SHA256.HashData(dib))}";

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        if (_source != null) _source.SelectedSessionChanged -= OnSelectedSessionChanged;
        _source = null;
        _pollTimer.Tick -= OnPollTimerTick;
        _remoteFiles?.Dispose();
    }
}

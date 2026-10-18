using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

/// <summary>Synchronizes the selected session with the host clipboard and owns its polling lifetime.</summary>
internal sealed class ViewportClipboardCoordinator : IDisposable
{
    private readonly Func<MainWindowViewModel?> _getViewModel;
    private readonly Func<IClipboard?> _getClipboard;
    private readonly Func<string[], Task<IStorageItem[]>> _createStorageItems;
    private readonly Func<Bitmap, byte[]?> _convertBitmapToDib;
    private readonly ClipboardSyncService _sync;
    private readonly IRemoteFileClipboard? _remoteFiles;
    private readonly Func<RemoteFileOffer, CancellationToken, Task<string[]>> _downloadFiles;
    private CancellationTokenSource? _download;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _polling;
    private bool _readErrorReported;
    private string? _lastInvalidFilePath;
    private int _generation;
    private bool _attached;
    private bool _disposed;
    private MainWindowViewModel? _subscribedViewModel;

    internal ViewportClipboardCoordinator(Func<MainWindowViewModel?> getViewModel, Func<IClipboard?> getClipboard,
        Func<string[], Task<IStorageItem[]>> createStorageItems, Func<Bitmap, byte[]?> convertBitmapToDib,
        ClipboardSyncService sync, IRemoteFileClipboard? remoteFiles = null,
        Func<RemoteFileOffer, CancellationToken, Task<string[]>>? downloadFiles = null)
    {
        _getViewModel = getViewModel;
        _getClipboard = getClipboard;
        _createStorageItems = createStorageItems;
        _convertBitmapToDib = convertBitmapToDib;
        _sync = sync;
        _remoteFiles = remoteFiles;
        _downloadFiles = downloadFiles ?? ((offer, token) => RemoteFileDownloader.DownloadAsync(offer, AppDataPaths.ClipboardCacheDirectory, token));
        _pollTimer.Tick += OnPollTimerTick;
    }

    internal void Start()
    {
        if (_disposed) return;
        _attached = true;
        SubscribeViewModel();
        _pollTimer.Start();
    }

    internal void Stop()
    {
        _attached = false;
        _generation++;
        CancelDownload();
        _pollTimer.Stop();
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
        _sync.UseSession(null);
    }

    internal void SessionChanged()
    {
        _generation++;
        CancelDownload();
        SubscribeViewModel();
        _sync.UseSession(_attached && !_disposed ? _getViewModel()?.SelectedSession : null);
    }

    private void SubscribeViewModel()
    {
        var viewModel = _attached ? _getViewModel() : null;
        if (ReferenceEquals(viewModel, _subscribedViewModel)) return;
        if (_subscribedViewModel != null) _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = viewModel;
        if (viewModel != null) viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedSession)) SessionChanged();
    }

    private void OnPollTimerTick(object? sender, EventArgs e) => _ = PollClipboardAsync();

    private bool IsCurrent(RdpSessionViewModel session, int generation) =>
        !_disposed && _attached && generation == _generation &&
        ReferenceEquals(session, _getViewModel()?.SelectedSession);

    internal async Task HandleRemoteTextAsync(RdpSessionViewModel session, string text)
    {
        if (!IsCurrent(session, _generation) || _getClipboard() is not { } clipboard) return;
        CancelDownload();
        try
        {
            _generation++;
            _sync.UseSession(session);
            _sync.BeginRemoteTextUpdate(text);
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            _sync.ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard text: {0}", ex);
        }
        finally
        {
            _sync.EndRemoteUpdate();
        }
    }

    internal async Task HandleRemoteFileOfferAsync(RdpSessionViewModel session, RemoteFileOffer offer)
    {
        if (!IsCurrent(session, _generation)) return;
        CancelDownload();
        _generation++;
        var generation = _generation;
        _sync.UseSession(session);
        if (_remoteFiles != null)
        {
            _sync.BeginRemoteUpdate();
            try
            {
                if (await _remoteFiles.OfferAsync(offer)) return;
            }
            finally
            {
                _sync.EndRemoteUpdate();
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

        if (IsCurrent(session, generation)) await HandleRemoteFilesAsync(session, paths);
    }

    private void CancelDownload()
    {
        var download = _download;
        _download = null;
        download?.Cancel();
    }

    internal async Task HandleRemoteFilesAsync(RdpSessionViewModel session, string[] paths)
    {
        if (paths.Length == 0 || !IsCurrent(session, _generation) || _getClipboard() is not { } clipboard) return;
        IStorageItem[]? items = null;
        try
        {
            _generation++;
            var generation = _generation;
            _sync.UseSession(session);
            _sync.BeginRemoteFilesUpdate(paths);
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
            _sync.ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard files: {0}", ex);
        }
        finally
        {
            _sync.EndRemoteUpdate();
        }
    }

    internal async Task PollClipboardAsync()
    {
        var session = _getViewModel()?.SelectedSession;
        if (session?.IsConnected != true)
        {
            _sync.UseSession(null);
            return;
        }

        _sync.UseSession(session);
        var clipboard = _getClipboard();
        if (_polling || !_sync.ShouldPollLocalClipboard || clipboard == null || !_attached || _remoteFiles?.OwnsClipboard == true) return;

        _polling = true;
        var generation = _generation;
        try
        {
            var data = await clipboard.TryGetDataAsync();
            if (!IsCurrent(session, generation) || !_sync.ShouldPollLocalClipboard) return;
            if (data == null)
            {
                _readErrorReported = false;
                // Announcing an empty clipboard takes ownership away from the server and fails a download in progress.
                if (_download == null && _sync.ObserveEmptyClipboard()) session.SetLocalClipboardText("");
                return;
            }

            var files = await data.TryGetFilesAsync();
            if (!IsCurrent(session, generation) || !_sync.ShouldPollLocalClipboard)
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
                    if (_sync.TryRememberFiles(paths.ToArray(), out _))
                    {
                        CancelDownload();
                        session.SetLocalClipboardFiles(paths.ToArray());
                    }
                    return;
                }
            }

            var text = await data.TryGetTextAsync();
            if (!IsCurrent(session, generation) || !_sync.ShouldPollLocalClipboard) return;
            if (!string.IsNullOrEmpty(text))
            {
                _readErrorReported = false;
                if (_sync.TryRememberText(text, out _))
                {
                    CancelDownload();
                    session.SetLocalClipboardText(text);
                }
                return;
            }

            var bitmap = await data.TryGetBitmapAsync();
            if (!IsCurrent(session, generation) || !_sync.ShouldPollLocalClipboard) return;
            _readErrorReported = false;
            if (bitmap != null && _sync.TryRememberBitmap(bitmap, out _))
            {
                var dib = _convertBitmapToDib(bitmap);
                if (dib != null)
                {
                    CancelDownload();
                    session.SetLocalClipboardBitmap(dib, (uint)bitmap.PixelSize.Width, (uint)bitmap.PixelSize.Height);
                }
                return;
            }

            if (bitmap == null && _download == null && _sync.ObserveEmptyClipboard()) session.SetLocalClipboardText("");
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

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _pollTimer.Tick -= OnPollTimerTick;
        _remoteFiles?.Dispose();
    }
}

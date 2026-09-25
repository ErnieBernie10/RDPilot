using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

internal sealed class RdpViewportPresenter
{
    private readonly Func<MainWindowViewModel?> _getViewModel;
    private readonly Func<IClipboard?> _getClipboard;
    private readonly Func<string[], Task<IStorageItem[]>> _createStorageItems;
    private readonly Func<Size> _getViewportSize;
    private readonly Action _invalidateViewport;
    private readonly Func<Bitmap, byte[]?> _convertBitmapToDib;
    private readonly ViewportResolutionService _viewportResolutionService;
    private readonly ViewportResolutionUpdateScheduler _viewportResolutionUpdateScheduler;
    private readonly PointerMoveScheduler _pointerMoveScheduler;
    private readonly ClipboardSyncService _clipboardSyncService;
    private bool _pollingClipboard;
    private int _remoteClipboardGeneration;
    private bool _clipboardReadErrorReported;
    private string? _lastInvalidClipboardFilePath;
    private readonly HashSet<Key> _pressedRdpKeys = new();
    private readonly HashSet<ushort> _pressedGrabbedScancodes = new();
    private bool _rdpKeyboardActive;
    private bool _keyboardGrabActive;
    private double _lastObservedScale = 1.0;

    public RdpViewportPresenter(
        Func<MainWindowViewModel?> getViewModel,
        Func<IClipboard?> getClipboard,
        Func<string[], Task<IStorageItem[]>> createStorageItems,
        Func<Size> getViewportSize,
        Action invalidateViewport,
        Func<Bitmap, byte[]?> convertBitmapToDib,
        ViewportResolutionService viewportResolutionService,
        ClipboardSyncService clipboardSyncService,
        ViewportResolutionUpdateScheduler viewportResolutionUpdateScheduler,
        PointerMoveScheduler pointerMoveScheduler)
    {
        _getViewModel = getViewModel;
        _getClipboard = getClipboard;
        _createStorageItems = createStorageItems;
        _getViewportSize = getViewportSize;
        _invalidateViewport = invalidateViewport;
        _convertBitmapToDib = convertBitmapToDib;
        _viewportResolutionService = viewportResolutionService;
        _clipboardSyncService = clipboardSyncService;
        _viewportResolutionUpdateScheduler = viewportResolutionUpdateScheduler;
        _pointerMoveScheduler = pointerMoveScheduler;
    }

    public void HandleSessionRedrawRequested()
    {
        _invalidateViewport();
    }

    public async Task HandleRemoteClipboardTextReceivedAsync(string text)
    {
        var clipboard = _getClipboard();
        if (clipboard == null)
        {
            return;
        }

        try
        {
            _remoteClipboardGeneration++;
            _clipboardSyncService.UseSession(_getViewModel()?.SelectedSession);
            _clipboardSyncService.BeginRemoteTextUpdate(text);
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            _clipboardSyncService.ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard text: {0}", ex);
        }
        finally
        {
            _clipboardSyncService.EndRemoteUpdate();
        }
    }

    public async Task HandleRemoteClipboardFilesReceivedAsync(string[] filePaths)
    {
        var clipboard = _getClipboard();
        if (clipboard == null || filePaths.Length == 0)
        {
            return;
        }

        try
        {
            _remoteClipboardGeneration++;
            _clipboardSyncService.UseSession(_getViewModel()?.SelectedSession);
            _clipboardSyncService.BeginRemoteFilesUpdate(filePaths);
            var items = await _createStorageItems(filePaths);
            if (items.Length == 0) throw new InvalidOperationException("No local clipboard files could be resolved.");
            await clipboard.SetFilesAsync(items);
        }
        catch (Exception ex)
        {
            _clipboardSyncService.ClearSignature();
            Trace.TraceWarning("Unable to set remote clipboard files: {0}", ex);
        }
        finally
        {
            _clipboardSyncService.EndRemoteUpdate();
        }
    }

    public async Task PollClipboardAsync()
    {
        var clipboard = _getClipboard();
        var vm = _getViewModel();
        var session = vm?.SelectedSession;
        if (session?.IsConnected != true)
        {
            _clipboardSyncService.UseSession(null);
            return;
        }

        _clipboardSyncService.UseSession(session);

        if (_pollingClipboard || !_clipboardSyncService.ShouldPollLocalClipboard || clipboard == null)
        {
            return;
        }

        _pollingClipboard = true;
        var generation = _remoteClipboardGeneration;
        try
        {
            var data = await clipboard.TryGetDataAsync();
            if (generation != _remoteClipboardGeneration || !_clipboardSyncService.ShouldPollLocalClipboard || !ReferenceEquals(session, _getViewModel()?.SelectedSession)) return;
            if (data == null)
            {
                _clipboardReadErrorReported = false;
                if (_clipboardSyncService.ObserveEmptyClipboard())
                {
                    session.SetLocalClipboardText("");
                }

                return;
            }

            var files = await data.TryGetFilesAsync();
            if (generation != _remoteClipboardGeneration || !_clipboardSyncService.ShouldPollLocalClipboard || !ReferenceEquals(session, _getViewModel()?.SelectedSession)) return;
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
                            _lastInvalidClipboardFilePath = null;
                            paths.Add(path);
                        }
                        else if ((path ?? "<no local path>") != _lastInvalidClipboardFilePath)
                        {
                            Trace.TraceWarning("Clipboard file has no readable local path (folders are not supported): {0}", path);
                            _lastInvalidClipboardFilePath = path ?? "<no local path>";
                        }
                    }
                }

                if (paths.Count > 0)
                {
                    _clipboardReadErrorReported = false;
                    if (_clipboardSyncService.TryRememberFiles(paths.ToArray(), out _))
                    {
                        session.SetLocalClipboardFiles(paths.ToArray());
                    }

                    return;
                }
            }

            var text = await data.TryGetTextAsync();
            if (generation != _remoteClipboardGeneration || !_clipboardSyncService.ShouldPollLocalClipboard || !ReferenceEquals(session, _getViewModel()?.SelectedSession)) return;
            if (!string.IsNullOrEmpty(text))
            {
                _clipboardReadErrorReported = false;
                if (_clipboardSyncService.TryRememberText(text, out _))
                {
                    session.SetLocalClipboardText(text);
                }

                return;
            }

            var bitmap = await data.TryGetBitmapAsync();
            if (generation != _remoteClipboardGeneration || !_clipboardSyncService.ShouldPollLocalClipboard || !ReferenceEquals(session, _getViewModel()?.SelectedSession)) return;
            _clipboardReadErrorReported = false;
            if (bitmap != null && _clipboardSyncService.TryRememberBitmap(bitmap, out _))
            {
                var dib = _convertBitmapToDib(bitmap);
                if (dib != null)
                {
                    session.SetLocalClipboardBitmap(dib, (uint)bitmap.PixelSize.Width, (uint)bitmap.PixelSize.Height);
                }
                return;
            }

            if (bitmap == null && _clipboardSyncService.ObserveEmptyClipboard())
            {
                session.SetLocalClipboardText("");
            }
        }
        catch (Exception ex)
        {
            if (!_clipboardReadErrorReported)
            {
                Trace.TraceWarning("Unable to read local clipboard: {0}", ex);
                _clipboardReadErrorReported = true;
            }
        }
        finally
        {
            _pollingClipboard = false;
        }
    }

    public void HandleScaleChanged(double scale, bool isMinimized)
    {
        if (Math.Abs(scale - _lastObservedScale) > 0.001)
        {
            _lastObservedScale = scale;
            QueueViewportResolutionUpdate(scale, isMinimized);
        }
    }

    public void QueueViewportResolutionUpdate(double scale, bool isMinimized)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

        _lastObservedScale = scale;
        var hasUsableSize = _viewportResolutionService.TryCompute(
            _getViewportSize(),
            scale,
            isMinimized,
            out var physWidth,
            out var physHeight,
            out var normalizedScale);

        // The scale is reported separately from the size because the two become available at
        // different times. A minimized window has no meaningful scale to report, but a restored
        // one whose viewport is still too small to drive a resolution update does - and the remote
        // DPI is locked at connect time, so missing it there is permanent for that session.
        if (!isMinimized)
        {
            vm.UpdateRenderScaling(normalizedScale);
        }

        if (hasUsableSize)
        {
            _viewportResolutionUpdateScheduler.Schedule(physWidth, physHeight, normalizedScale, vm.UpdateResolution);
            return;
        }

        _viewportResolutionUpdateScheduler.Cancel();
    }

    public void CancelViewportResolutionUpdates()
    {
        _viewportResolutionUpdateScheduler.Cancel();
        _pointerMoveScheduler.Cancel();
    }

    public void HandlePointerMoved(double x, double y)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

        _pointerMoveScheduler.Schedule(x, y, (dipX, dipY) => vm.SendMouseEventScaled(RdpPointerInputMapper.PointerMoveFlag, dipX, dipY));
    }

    public void HandlePointerPressed(double x, double y, PointerPointProperties properties)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

        _pointerMoveScheduler.Flush();
        ushort flags = RdpPointerInputMapper.PointerDownFlag;
        if (properties.IsLeftButtonPressed) flags |= RdpPointerInputMapper.PointerButton1Flag;
        if (properties.IsRightButtonPressed) flags |= RdpPointerInputMapper.PointerButton2Flag;
        if (properties.IsMiddleButtonPressed) flags |= RdpPointerInputMapper.PointerButton3Flag;
        vm.SendMouseEventScaled(flags, x, y);
    }

    public void HandlePointerReleased(double x, double y, PointerUpdateKind updateKind)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

        _pointerMoveScheduler.Flush();
        ushort flags = 0;
        switch (updateKind)
        {
            case PointerUpdateKind.LeftButtonReleased:
                flags |= RdpPointerInputMapper.PointerButton1Flag;
                break;
            case PointerUpdateKind.RightButtonReleased:
                flags |= RdpPointerInputMapper.PointerButton2Flag;
                break;
            case PointerUpdateKind.MiddleButtonReleased:
                flags |= RdpPointerInputMapper.PointerButton3Flag;
                break;
        }

        vm.SendMouseEventScaled(flags, x, y);
    }

    public void HandlePointerWheelChanged(double x, double y, Vector delta)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

        _pointerMoveScheduler.Flush();
        SendWheelDelta(vm, RdpPointerInputMapper.PointerWheelFlag, delta.Y, x, y);
        SendWheelDelta(vm, RdpPointerInputMapper.PointerHorizontalWheelFlag, delta.X, x, y);
    }

    public bool HandleKeyDown(object? source, object rdpImage, Key key)
    {
        if (!ShouldHandleKeyboardEvent(source, rdpImage))
        {
            return false;
        }

        if (!RdpKeyboardInputMapper.TryMapKey(key, out var scancode))
        {
            return false;
        }

        var vm = _getViewModel();
        if (vm == null)
        {
            return false;
        }

        ushort flags = RdpKeyboardInputMapper.BuildKeyFlags(scancode, isRelease: false, out scancode);

        _rdpKeyboardActive = true;
        _pressedRdpKeys.Add(key);
        vm.SendKeyboardEvent(flags, scancode);
        return true;
    }

    public bool HandleKeyUp(object? source, object rdpImage, Key key)
    {
        if (!ShouldHandleKeyboardEvent(source, rdpImage))
        {
            return false;
        }

        if (!RdpKeyboardInputMapper.TryMapKey(key, out var scancode))
        {
            return false;
        }

        var vm = _getViewModel();
        if (vm == null)
        {
            return false;
        }

        ushort flags = RdpKeyboardInputMapper.BuildKeyFlags(scancode, isRelease: true, out scancode);

        vm.SendKeyboardEvent(flags, scancode);
        _pressedRdpKeys.Remove(key);
        _rdpKeyboardActive = _pressedRdpKeys.Count > 0;
        return true;
    }

    public void ReleasePressedRdpKeys()
    {
        _pointerMoveScheduler.Cancel();
        var vm = _getViewModel();
        if (_pressedRdpKeys.Count == 0 || vm == null)
        {
            _pressedRdpKeys.Clear();
            _rdpKeyboardActive = false;
            return;
        }

        foreach (var key in _pressedRdpKeys)
        {
            if (!RdpKeyboardInputMapper.TryMapKey(key, out var scancode))
            {
                continue;
            }

            var flags = RdpKeyboardInputMapper.BuildKeyFlags(scancode, isRelease: true, out scancode);
            vm.SendKeyboardEvent(flags, scancode);
        }

        _pressedRdpKeys.Clear();
        _rdpKeyboardActive = false;
    }

    /// <summary>
    /// Engages or releases keyboard grab. While grabbed the platform hook is the sole keyboard
    /// source, so the Avalonia path is shut off and each path flushes its own held keys on the
    /// transition to avoid stuck modifiers on the remote host.
    /// </summary>
    public void SetKeyboardGrabActive(bool active)
    {
        if (_keyboardGrabActive == active)
        {
            return;
        }

        if (active)
        {
            ReleasePressedRdpKeys();
            _keyboardGrabActive = true;
        }
        else
        {
            _keyboardGrabActive = false;
            ReleaseGrabbedKeys();
        }
    }

    public bool HandleGrabbedKey(ushort scancode, bool extended, bool isUp)
    {
        var vm = _getViewModel();
        if (vm == null || scancode == 0)
        {
            return false;
        }

        var trackedScancode = (ushort)(scancode | (extended ? RdpKeyboardInputMapper.ExtendedScancodeBit : 0));
        var flags = RdpKeyboardInputMapper.BuildKeyFlags(trackedScancode, isUp, out var normalizedScancode);
        vm.SendKeyboardEvent(flags, normalizedScancode);

        if (isUp)
        {
            _pressedGrabbedScancodes.Remove(trackedScancode);
        }
        else
        {
            _pressedGrabbedScancodes.Add(trackedScancode);
        }

        return true;
    }

    public void ReleaseGrabbedKeys()
    {
        var vm = _getViewModel();
        if (_pressedGrabbedScancodes.Count == 0 || vm == null)
        {
            _pressedGrabbedScancodes.Clear();
            return;
        }

        foreach (var trackedScancode in _pressedGrabbedScancodes)
        {
            var flags = RdpKeyboardInputMapper.BuildKeyFlags(trackedScancode, isRelease: true, out var normalizedScancode);
            vm.SendKeyboardEvent(flags, normalizedScancode);
        }

        _pressedGrabbedScancodes.Clear();
    }

    private bool ShouldHandleKeyboardEvent(object? source, object rdpImage)
    {
        if (_keyboardGrabActive)
        {
            return false;
        }

        return ReferenceEquals(source, rdpImage) || _rdpKeyboardActive;
    }

    private static void SendWheelDelta(MainWindowViewModel vm, ushort wheelFlag, double delta, double dipX, double dipY)
    {
        if (delta == 0)
        {
            return;
        }

        var flags = RdpPointerInputMapper.BuildWheelFlags(wheelFlag, delta);
        if (flags != 0)
        {
            vm.SendMouseEventScaled(flags, dipX, dipY);
        }
    }
}

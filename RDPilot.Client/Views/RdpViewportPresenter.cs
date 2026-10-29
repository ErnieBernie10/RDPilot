using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

internal sealed class RdpViewportPresenter : IDisposable
{
    private readonly Func<MainWindowViewModel?> _getViewModel;
    private readonly Func<Size> _getViewportSize;
    private readonly Action _invalidateViewport;
    private readonly ViewportResolutionService _viewportResolutionService;
    private readonly ViewportResolutionUpdateScheduler _viewportResolutionUpdateScheduler;
    private readonly PointerMoveScheduler _pointerMoveScheduler;
    private readonly ViewportClipboardCoordinator _clipboard;
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
        _getViewportSize = getViewportSize;
        _invalidateViewport = invalidateViewport;
        _viewportResolutionService = viewportResolutionService;
        _clipboard = new ViewportClipboardCoordinator(getViewModel, getClipboard, createStorageItems, convertBitmapToDib, clipboardSyncService,
            OperatingSystem.IsWindows() ? new WindowsVirtualFileClipboard() : null);
        _viewportResolutionUpdateScheduler = viewportResolutionUpdateScheduler;
        _pointerMoveScheduler = pointerMoveScheduler;
    }

    public void HandleSessionRedrawRequested()
    {
        _invalidateViewport();
    }

    public Task PollClipboardAsync() => _clipboard.PollClipboardAsync();

    public void StartClipboardPolling() => _clipboard.Start();

    public void StopClipboardPolling() => _clipboard.Stop();

    public void ClipboardSessionChanged() => _clipboard.SessionChanged();

    public void Dispose() => _clipboard.Dispose();

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

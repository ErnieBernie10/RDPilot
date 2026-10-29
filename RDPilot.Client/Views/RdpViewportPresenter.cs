using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

internal sealed class RdpViewportPresenter
{
    private readonly Func<MainWindowViewModel?> _getViewModel;
    private readonly Func<Size> _getViewportSize;
    private readonly Action _invalidateViewport;
    private readonly ViewportResolutionService _viewportResolutionService;
    private readonly ViewportResolutionUpdateScheduler _viewportResolutionUpdateScheduler;
    private readonly PointerInputSender _pointerInput;
    private readonly HashSet<Key> _pressedRdpKeys = new();
    private readonly HashSet<ushort> _pressedGrabbedScancodes = new();
    private bool _rdpKeyboardActive;
    private bool _keyboardGrabActive;
    private double _lastObservedScale = 1.0;

    public RdpViewportPresenter(
        Func<MainWindowViewModel?> getViewModel,
        Func<Size> getViewportSize,
        Action invalidateViewport,
        ViewportResolutionService viewportResolutionService,
        ViewportResolutionUpdateScheduler viewportResolutionUpdateScheduler,
        PointerInputSender pointerInput)
    {
        _getViewModel = getViewModel;
        _getViewportSize = getViewportSize;
        _invalidateViewport = invalidateViewport;
        _viewportResolutionService = viewportResolutionService;
        _viewportResolutionUpdateScheduler = viewportResolutionUpdateScheduler;
        _pointerInput = pointerInput;
    }

    public void HandleSessionRedrawRequested()
    {
        _invalidateViewport();
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
        _pointerInput.Cancel();
    }

    public void HandlePointerMoved(double x, double y) => _pointerInput.Move(x, y);

    public void HandlePointerPressed(double x, double y, PointerPointProperties properties) => _pointerInput.Press(x, y, properties);

    public void HandlePointerReleased(double x, double y, PointerUpdateKind updateKind) => _pointerInput.Release(x, y, updateKind);

    public void HandlePointerWheelChanged(double x, double y, Vector delta) => _pointerInput.Wheel(x, y, delta);

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
        SendKey(vm, flags, scancode);
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

        SendKey(vm, flags, scancode);
        _pressedRdpKeys.Remove(key);
        _rdpKeyboardActive = _pressedRdpKeys.Count > 0;
        return true;
    }

    public void ReleasePressedRdpKeys()
    {
        _pointerInput.Cancel();
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
            SendKey(vm, flags, scancode);
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
        SendKey(vm, flags, normalizedScancode);

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
            SendKey(vm, flags, normalizedScancode);
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

    private static void SendKey(MainWindowViewModel vm, ushort flags, ushort scancode) =>
        vm.SelectedSession?.Send(new KeyInput(flags, scancode));
}

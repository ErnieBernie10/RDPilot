using System;
using System.Collections.Generic;
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
    private readonly Func<Size> _getViewportSize;
    private readonly Action _invalidateViewport;
    private readonly Func<Bitmap, byte[]?> _convertBitmapToDib;
    private readonly ViewportResolutionService _viewportResolutionService;
    private readonly ClipboardSyncService _clipboardSyncService;
    private readonly HashSet<Key> _pressedRdpKeys = new();
    private bool _rdpKeyboardActive;
    private int _keyboardLogCount;
    private int _textInputLogCount;
    private double _lastObservedScale = 1.0;

    public RdpViewportPresenter(
        Func<MainWindowViewModel?> getViewModel,
        Func<IClipboard?> getClipboard,
        Func<Size> getViewportSize,
        Action invalidateViewport,
        Func<Bitmap, byte[]?> convertBitmapToDib,
        ViewportResolutionService viewportResolutionService,
        ClipboardSyncService clipboardSyncService)
    {
        _getViewModel = getViewModel;
        _getClipboard = getClipboard;
        _getViewportSize = getViewportSize;
        _invalidateViewport = invalidateViewport;
        _convertBitmapToDib = convertBitmapToDib;
        _viewportResolutionService = viewportResolutionService;
        _clipboardSyncService = clipboardSyncService;
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
            _clipboardSyncService.BeginRemoteTextUpdate(text);
            await clipboard.SetTextAsync(text);
            Console.WriteLine($"[CLIPRDR] set local clipboard from remote chars={text.Length}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIPRDR] failed to set local clipboard: {ex.Message}");
        }
        finally
        {
            _clipboardSyncService.EndRemoteTextUpdate();
        }
    }

    public async Task PollClipboardAsync()
    {
        var clipboard = _getClipboard();
        var vm = _getViewModel();
        if (!_clipboardSyncService.ShouldPollLocalClipboard || clipboard == null)
        {
            return;
        }

        try
        {
            var data = await clipboard.TryGetDataAsync();
            if (data == null)
            {
                if (_clipboardSyncService.ClearSignature() && vm != null)
                {
                    vm.SetLocalClipboardText("");
                }

                return;
            }

            var files = await data.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                var paths = new List<string>(files.Length);
                foreach (var item in files)
                {
                    using (item)
                    {
                        var path = item.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            paths.Add(path);
                        }
                    }
                }

                if (paths.Count > 0)
                {
                    if (_clipboardSyncService.TryRememberFiles(paths.ToArray(), out _) && vm != null)
                    {
                        vm.SetLocalClipboardFiles(paths.ToArray());
                    }

                    return;
                }
            }

            var text = await data.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                if (_clipboardSyncService.TryRememberText(text, out _))
                {
                    Console.WriteLine($"[CLIPRDR] local clipboard text chars={text.Length}");
                    vm?.SetLocalClipboardText(text);
                }

                return;
            }

            var bitmap = await data.TryGetBitmapAsync();
            if (bitmap != null && _clipboardSyncService.TryRememberBitmap(bitmap, out _) && vm != null)
            {
                var dib = _convertBitmapToDib(bitmap);
                if (dib != null)
                {
                    vm.SetLocalClipboardBitmap(dib, (uint)bitmap.PixelSize.Width, (uint)bitmap.PixelSize.Height);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIPRDR] failed to poll local clipboard: {ex.Message}");
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
        if (_viewportResolutionService.TryCompute(
                _getViewportSize(),
                scale,
                isMinimized,
                out var physWidth,
                out var physHeight,
                out var normalizedScale))
        {
            vm.UpdateResolution(physWidth, physHeight, normalizedScale);
        }
    }

    public void HandlePointerMoved(double x, double y)
    {
        _getViewModel()?.SendMouseEventScaled(RdpPointerInputMapper.PointerMoveFlag, x, y);
    }

    public void HandlePointerPressed(double x, double y, PointerPointProperties properties)
    {
        var vm = _getViewModel();
        if (vm == null)
        {
            return;
        }

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

        ushort originalScancode = scancode;
        ushort flags = RdpKeyboardInputMapper.BuildKeyFlags(scancode, isRelease: false, out scancode);

        LogKeyboardEvent("down", key, flags, scancode, originalScancode);
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

        ushort originalScancode = scancode;
        ushort flags = RdpKeyboardInputMapper.BuildKeyFlags(scancode, isRelease: true, out scancode);

        LogKeyboardEvent("up", key, flags, scancode, originalScancode);
        vm.SendKeyboardEvent(flags, scancode);
        _pressedRdpKeys.Remove(key);
        _rdpKeyboardActive = _pressedRdpKeys.Count > 0;
        return true;
    }

    public void HandleTextInput(object? source, object rdpImage, string? text)
    {
        if (!ReferenceEquals(source, rdpImage) || string.IsNullOrEmpty(text) || _textInputLogCount >= 32)
        {
            return;
        }

        _textInputLogCount++;
        Console.WriteLine($"[KEYBOARD] phase=avalonia-text text={FormatTextForLog(text)} length={text.Length}");
    }

    public void ReleasePressedRdpKeys()
    {
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

    private bool ShouldHandleKeyboardEvent(object? source, object rdpImage)
    {
        return ReferenceEquals(source, rdpImage) || _rdpKeyboardActive;
    }

    private void LogKeyboardEvent(string phase, Key key, ushort flags, ushort scancode, ushort originalScancode)
    {
        if (_keyboardLogCount >= 32)
        {
            return;
        }

        _keyboardLogCount++;
        Console.WriteLine($"[KEYBOARD] phase=avalonia-{phase} key={key} flags=0x{flags:X4} scancode=0x{scancode:X2} original=0x{originalScancode:X3}");
    }

    private static string FormatTextForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
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

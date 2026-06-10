using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using RDP.Client.ViewModels;

namespace RDP.Client.Views;

public partial class MainWindow : Window
{
    private const int MinimumRemoteWidth = 640;
    private const int MinimumRemoteHeight = 480;
    private bool _rdpKeyboardActive;
    private readonly HashSet<Key> _pressedRdpKeys = new();
    private readonly DispatcherTimer _clipboardPollTimer;
    private readonly NativeWrapper.ClipboardTextCallback _clipboardTextCallback;
    private string? _lastClipboardText;
    private bool _settingClipboardFromRemote;

    public MainWindow()
    {
        InitializeComponent();
        _clipboardTextCallback = OnRemoteClipboardTextReceived;
        NativeWrapper.set_clipboard_text_callback(_clipboardTextCallback);
        _clipboardPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _clipboardPollTimer.Tick += async (_, _) => await PollClipboardAsync();

        DataContextChanged += (sender, args) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RequestRedraw += (s, e) => RdpImage.InvalidateVisual();
                QueueViewportResolutionUpdate();
            }
        };

        SizeChanged += OnSizeChanged;
        Deactivated += (_, _) => ReleasePressedRdpKeys();
        Opened += (_, _) =>
        {
            QueueViewportResolutionUpdate();
            _clipboardPollTimer.Start();
        };
        Closed += (_, _) => _clipboardPollTimer.Stop();
        RdpImage.LostFocus += (_, _) => ReleasePressedRdpKeys();
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, true);
    }

    private void OnRemoteClipboardTextReceived(IntPtr textPtr)
    {
        var text = Marshal.PtrToStringUTF8(textPtr) ?? "";
        Dispatcher.UIThread.Post(async () =>
        {
            var clipboard = Clipboard;
            if (clipboard == null)
            {
                return;
            }

            try
            {
                _settingClipboardFromRemote = true;
                await clipboard.SetTextAsync(text);
                _lastClipboardText = text;
                Console.WriteLine($"[CLIPRDR] set local clipboard from remote chars={text.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIPRDR] failed to set local clipboard: {ex.Message}");
            }
            finally
            {
                _settingClipboardFromRemote = false;
            }
        });
    }

    private async System.Threading.Tasks.Task PollClipboardAsync()
    {
        if (_settingClipboardFromRemote || Clipboard == null)
        {
            return;
        }

        try
        {
            var text = await Clipboard.TryGetTextAsync() ?? "";
            if (text.Length == 0)
            {
                return;
            }

            if (text == _lastClipboardText)
            {
                return;
            }

            _lastClipboardText = text;
            NativeWrapper.clipboard_set_local_text(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIPRDR] failed to poll local clipboard: {ex.Message}");
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueViewportResolutionUpdate();
    }

    private void OnRdpViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueViewportResolutionUpdate();
    }

    private void QueueViewportResolutionUpdate()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            var size = RdpScrollViewer.Bounds.Size;
            if (size.Width >= MinimumRemoteWidth && size.Height >= MinimumRemoteHeight)
            {
                vm.UpdateResolution((int)size.Width, (int)size.Height);
            }
        }
    }

    private const ushort PTR_FLAGS_MOVE = 0x0800;
    private const ushort PTR_FLAGS_DOWN = 0x8000;
    private const ushort PTR_FLAGS_BUTTON1 = 0x1000;
    private const ushort PTR_FLAGS_BUTTON2 = 0x2000;
    private const ushort PTR_FLAGS_BUTTON3 = 0x4000;
    private const ushort PTR_FLAGS_WHEEL = 0x0200;
    private const ushort PTR_FLAGS_HWHEEL = 0x0400;
    private const ushort PTR_FLAGS_WHEEL_NEGATIVE = 0x0100;
    private const int WheelDelta = 120;

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SendMouseEvent(PTR_FLAGS_MOVE, (ushort)pos.X, (ushort)pos.Y);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        ushort flags = PTR_FLAGS_DOWN;
        var point = e.GetCurrentPoint(RdpImage);
        if (point.Properties.IsLeftButtonPressed) flags |= PTR_FLAGS_BUTTON1;
        if (point.Properties.IsRightButtonPressed) flags |= PTR_FLAGS_BUTTON2;
        if (point.Properties.IsMiddleButtonPressed) flags |= PTR_FLAGS_BUTTON3;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SendMouseEvent(flags, (ushort)pos.X, (ushort)pos.Y);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        ushort flags = 0; // Release is absence of DOWN
        var point = e.GetCurrentPoint(RdpImage);
        // In released event, the button that was released might already be false in Properties
        // Avalonia's PointerUpdateKind can tell us which button changed
        switch (e.GetCurrentPoint(RdpImage).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonReleased:
                flags |= PTR_FLAGS_BUTTON1;
                break;
            case PointerUpdateKind.RightButtonReleased:
                flags |= PTR_FLAGS_BUTTON2;
                break;
            case PointerUpdateKind.MiddleButtonReleased:
                flags |= PTR_FLAGS_BUTTON3;
                break;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SendMouseEvent(flags, (ushort)pos.X, (ushort)pos.Y);
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var pos = e.GetPosition(RdpImage);
        SendWheelDelta(vm, PTR_FLAGS_WHEEL, e.Delta.Y, (ushort)pos.X, (ushort)pos.Y);
        SendWheelDelta(vm, PTR_FLAGS_HWHEEL, e.Delta.X, (ushort)pos.X, (ushort)pos.Y);
        e.Handled = true;
    }

    private static void SendWheelDelta(MainWindowViewModel vm, ushort wheelFlag, double delta, ushort x, ushort y)
    {
        if (delta == 0)
        {
            return;
        }

        var wheelDelta = (int)Math.Round(Math.Abs(delta) * WheelDelta);
        if (wheelDelta == 0)
        {
            wheelDelta = 1;
        }

        ushort flags = (ushort)(wheelFlag | Math.Min(wheelDelta, 0xFF));
        if (delta < 0)
        {
            flags |= PTR_FLAGS_WHEEL_NEGATIVE;
        }

        vm.SendMouseEvent(flags, x, y);
    }

    private const ushort KBD_FLAGS_RELEASE = 0x8000;
    private const ushort KBD_FLAGS_EXTENDED = 0x0100;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ShouldHandleKeyboardEvent(e))
        {
            return;
        }

        ushort scancode = KeyToScancode(e.Key);
        if (scancode != 0)
        {
            ushort flags = 0;
            if ((scancode & 0x100) != 0)
            {
                flags |= KBD_FLAGS_EXTENDED;
                scancode &= 0xFF;
            }
            if (DataContext is MainWindowViewModel vm)
            {
                _rdpKeyboardActive = true;
                _pressedRdpKeys.Add(e.Key);
                vm.SendKeyboardEvent(flags, scancode);
                e.Handled = true;
            }
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!ShouldHandleKeyboardEvent(e))
        {
            return;
        }

        ushort scancode = KeyToScancode(e.Key);
        if (scancode != 0)
        {
            ushort flags = KBD_FLAGS_RELEASE;
            if ((scancode & 0x100) != 0)
            {
                flags |= KBD_FLAGS_EXTENDED;
                scancode &= 0xFF;
            }
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SendKeyboardEvent(flags, scancode);
                _pressedRdpKeys.Remove(e.Key);
                _rdpKeyboardActive = _pressedRdpKeys.Count > 0;
                e.Handled = true;
            }
        }
    }

    private bool ShouldHandleKeyboardEvent(KeyEventArgs e)
    {
        return ReferenceEquals(e.Source, RdpImage) || _rdpKeyboardActive;
    }

    private void ReleasePressedRdpKeys()
    {
        if (_pressedRdpKeys.Count == 0 || DataContext is not MainWindowViewModel vm)
        {
            _pressedRdpKeys.Clear();
            _rdpKeyboardActive = false;
            return;
        }

        foreach (var key in _pressedRdpKeys)
        {
            ushort scancode = KeyToScancode(key);
            if (scancode == 0)
            {
                continue;
            }

            ushort flags = KBD_FLAGS_RELEASE;
            if ((scancode & 0x100) != 0)
            {
                flags |= KBD_FLAGS_EXTENDED;
                scancode &= 0xFF;
            }

            vm.SendKeyboardEvent(flags, scancode);
        }

        _pressedRdpKeys.Clear();
        _rdpKeyboardActive = false;
    }

    private ushort KeyToScancode(Key key)
    {
        return key switch
        {
            Key.Escape => 0x01,
            Key.D1 => 0x02,
            Key.D2 => 0x03,
            Key.D3 => 0x04,
            Key.D4 => 0x05,
            Key.D5 => 0x06,
            Key.D6 => 0x07,
            Key.D7 => 0x08,
            Key.D8 => 0x09,
            Key.D9 => 0x0A,
            Key.D0 => 0x0B,
            Key.OemMinus => 0x0C,
            Key.OemPlus => 0x0D,
            Key.Back => 0x0E,
            Key.Tab => 0x0F,
            Key.Q => 0x10,
            Key.W => 0x11,
            Key.E => 0x12,
            Key.R => 0x13,
            Key.T => 0x14,
            Key.Y => 0x15,
            Key.U => 0x16,
            Key.I => 0x17,
            Key.O => 0x18,
            Key.P => 0x19,
            Key.OemOpenBrackets => 0x1A,
            Key.OemCloseBrackets => 0x1B,
            Key.Enter => 0x1C,
            Key.LeftCtrl => 0x1D,
            Key.A => 0x1E,
            Key.S => 0x1F,
            Key.D => 0x20,
            Key.F => 0x21,
            Key.G => 0x22,
            Key.H => 0x23,
            Key.J => 0x24,
            Key.K => 0x25,
            Key.L => 0x26,
            Key.OemSemicolon => 0x27,
            Key.OemQuotes => 0x28,
            Key.OemTilde => 0x29,
            Key.LeftShift => 0x2A,
            Key.OemBackslash => 0x2B,
            Key.Z => 0x2C,
            Key.X => 0x2D,
            Key.C => 0x2E,
            Key.V => 0x2F,
            Key.B => 0x30,
            Key.N => 0x31,
            Key.M => 0x32,
            Key.OemComma => 0x33,
            Key.OemPeriod => 0x34,
            Key.OemQuestion => 0x35,
            Key.RightShift => 0x36,
            Key.Multiply => 0x37,
            Key.LeftAlt => 0x38,
            Key.Space => 0x39,
            Key.CapsLock => 0x3A,
            Key.F1 => 0x3B,
            Key.F2 => 0x3C,
            Key.F3 => 0x3D,
            Key.F4 => 0x3E,
            Key.F5 => 0x3F,
            Key.F6 => 0x40,
            Key.F7 => 0x41,
            Key.F8 => 0x42,
            Key.F9 => 0x43,
            Key.F10 => 0x44,
            Key.NumLock => 0x45,
            Key.Scroll => 0x46,
            Key.NumPad7 => 0x47,
            Key.NumPad8 => 0x48,
            Key.NumPad9 => 0x49,
            Key.Subtract => 0x4A,
            Key.NumPad4 => 0x4B,
            Key.NumPad5 => 0x4C,
            Key.NumPad6 => 0x4D,
            Key.Add => 0x4E,
            Key.NumPad1 => 0x4F,
            Key.NumPad2 => 0x50,
            Key.NumPad3 => 0x51,
            Key.NumPad0 => 0x52,
            Key.Decimal => 0x53,
            Key.F11 => 0x57,
            Key.F12 => 0x58,
            Key.Home => 0x147,
            Key.Up => 0x148,
            Key.PageUp => 0x149,
            Key.Divide => 0x135,
            Key.Left => 0x14B,
            Key.Right => 0x14D,
            Key.End => 0x14F,
            Key.Down => 0x150,
            Key.PageDown => 0x151,
            Key.Insert => 0x152,
            Key.Delete => 0x153,
            Key.RightCtrl => 0x11D,
            Key.RightAlt => 0x138,
            _ => 0
        };
    }
}

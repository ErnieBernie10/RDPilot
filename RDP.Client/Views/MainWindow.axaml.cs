using System;
using Avalonia.Controls;
using Avalonia.Input;
using RDP.Client.ViewModels;

namespace RDP.Client.Views;

public partial class MainWindow : Window
{
    private const int MinimumRemoteWidth = 640;
    private const int MinimumRemoteHeight = 480;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (sender, args) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RequestRedraw += (s, e) => RdpImage.InvalidateVisual();
                QueueViewportResolutionUpdate();
            }
        };

        SizeChanged += OnSizeChanged;
        Opened += (_, _) => QueueViewportResolutionUpdate();
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

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        NativeWrapper.send_mouse_event(PTR_FLAGS_MOVE, (ushort)pos.X, (ushort)pos.Y);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        ushort flags = PTR_FLAGS_DOWN;
        var point = e.GetCurrentPoint(RdpImage);
        if (point.Properties.IsLeftButtonPressed) flags |= PTR_FLAGS_BUTTON1;
        if (point.Properties.IsRightButtonPressed) flags |= PTR_FLAGS_BUTTON2;
        if (point.Properties.IsMiddleButtonPressed) flags |= PTR_FLAGS_BUTTON3;
        NativeWrapper.send_mouse_event(flags, (ushort)pos.X, (ushort)pos.Y);
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
        NativeWrapper.send_mouse_event(flags, (ushort)pos.X, (ushort)pos.Y);
    }

    private const ushort KBD_FLAGS_RELEASE = 0x8000;
    private const ushort KBD_FLAGS_EXTENDED = 0x0100;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        ushort scancode = KeyToScancode(e.Key);
        if (scancode != 0)
        {
            ushort flags = 0;
            if ((scancode & 0x100) != 0)
            {
                flags |= KBD_FLAGS_EXTENDED;
                scancode &= 0xFF;
            }
            NativeWrapper.send_keyboard_event(flags, scancode);
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        ushort scancode = KeyToScancode(e.Key);
        if (scancode != 0)
        {
            ushort flags = KBD_FLAGS_RELEASE;
            if ((scancode & 0x100) != 0)
            {
                flags |= KBD_FLAGS_EXTENDED;
                scancode &= 0xFF;
            }
            NativeWrapper.send_keyboard_event(flags, scancode);
        }
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
            Key.LeftShift => 0x2A,
            Key.Z => 0x2C,
            Key.X => 0x2D,
            Key.C => 0x2E,
            Key.V => 0x2F,
            Key.B => 0x30,
            Key.N => 0x31,
            Key.M => 0x32,
            Key.RightShift => 0x36,
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
            Key.Home => 0x147,
            Key.Up => 0x148,
            Key.PageUp => 0x149,
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

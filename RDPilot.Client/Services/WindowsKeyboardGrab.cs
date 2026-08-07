using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RDPilot.Client.Services;

/// <summary>
/// Captures the physical keyboard with a WH_KEYBOARD_LL hook, the same mechanism mstsc uses.
/// While engaged the hook is the sole keyboard source: every key is suppressed locally and
/// raised through <see cref="KeyIntercepted"/> instead, so the local shell never sees the
/// Windows key, Alt+Tab or Ctrl+Esc.
/// </summary>
/// <remarks>
/// The hook callback runs on the thread that installed it - the Avalonia UI thread, which
/// already pumps a Win32 message loop. It must stay fast, lock-free and log-free: exceeding
/// LowLevelHooksTimeout (~300 ms) makes Windows silently drop the hook.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeyboardGrab : IKeyboardGrab
{
    private const int WhKeyboardLowLevel = 13;
    private const int HcAction = 0;
    private static readonly IntPtr SuppressKey = new(1);

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;
    private IntPtr _windowHandle;
    private bool _engaged;
    private bool _disposed;

    public WindowsKeyboardGrab()
    {
        // Held in a field so the delegate is not collected while the hook is installed.
        _callback = OnKeyboardEvent;
    }

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool IsEngaged => _engaged;

    public event EventHandler<GrabbedKeyEventArgs>? KeyIntercepted;

    public void Attach(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public void SetEngaged(bool engaged)
    {
        if (_disposed)
        {
            return;
        }

        _engaged = engaged;

        if (!engaged)
        {
            UninstallHook();
            return;
        }

        // Idempotent, and re-arms after Windows drops a hook that overran its timeout.
        InstallHook();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engaged = false;
        UninstallHook();
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        var moduleHandle = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WhKeyboardLowLevel, _callback, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            Console.WriteLine($"[KEYGRAB] SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private void UninstallHook()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr OnKeyboardEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode != HcAction || !_engaged || GetForegroundWindow() != _windowHandle)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // KBDLLHOOKSTRUCT: vkCode, scanCode, flags, time, dwExtraInfo.
            var virtualKeyCode = (uint)Marshal.ReadInt32(lParam, 0);
            var scanCode = (uint)Marshal.ReadInt32(lParam, 4);
            var hookFlags = (uint)Marshal.ReadInt32(lParam, 8);

            if (!KeyboardGrabScanCodeMapper.TryMapHookEvent(
                    virtualKeyCode,
                    scanCode,
                    hookFlags,
                    out var scancode,
                    out var extended,
                    out var isUp))
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            KeyIntercepted?.Invoke(this, new GrabbedKeyEventArgs(scancode, extended, isUp));
            return SuppressKey;
        }
        catch
        {
            // An exception escaping a hook callback tears down the process; always fall through.
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

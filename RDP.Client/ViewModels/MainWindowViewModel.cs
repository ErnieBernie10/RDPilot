using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RDP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _gatewayHost;
    [ObservableProperty] private string _gatewayDomain;
    [ObservableProperty] private string _gatewayUsername;
    [ObservableProperty] private string _gatewayPassword;
    [ObservableProperty] private WriteableBitmap? _screen;

    private readonly NativeWrapper.FrameCallback _frameCallback;

    public MainWindowViewModel()
    {
        _frameCallback = OnFrameReceived;
        NativeWrapper.set_frame_callback(_frameCallback);
    }

    [RelayCommand]
    private void Connect()
    {
        // For now we use a fixed resolution. In a more complete implementation,
        // we might want to use the actual window size.
        int width = 1280;
        int height = 720;

        if (Screen == null || Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
        {
            Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        NativeWrapper.connect_rdp(Host, Domain, Username, Password, GatewayHost, GatewayDomain, GatewayUsername, GatewayPassword, width, height);
    }

    [RelayCommand]
    private void Disconnect()
    {
        NativeWrapper.disconnect_rdp();
    }

    private void OnFrameReceived(IntPtr data, int width, int height)
    {
        var currentScreen = Screen;
        if (currentScreen == null || currentScreen.PixelSize.Width != width || currentScreen.PixelSize.Height != height)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Screen == null || Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
                {
                    Console.WriteLine($"[DEBUG_LOG] Resizing Screen to {width}x{height}");
                    Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                }
            });
            return;
        }

        using (var lockedBitmap = currentScreen.Lock())
        {
            var size = width * height * 4;
            unsafe
            {
                Buffer.MemoryCopy(data.ToPointer(), lockedBitmap.Address.ToPointer(), size, size);
            }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(Screen));
            RequestRedraw?.Invoke(this, EventArgs.Empty);
        });
    }

    public event EventHandler? RequestRedraw;
}
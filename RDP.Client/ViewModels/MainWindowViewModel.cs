using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RDP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private WriteableBitmap? _screen;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _gatewayHost = "";
    [ObservableProperty] private string _gatewayDomain = "";
    [ObservableProperty] private string _gatewayUsername = "";
    [ObservableProperty] private string _gatewayPassword = "";
    private readonly NativeWrapper.FrameCallback _frameCallback;
    private int _requestedWidth = 1280;
    private int _requestedHeight = 720;

    public MainWindowViewModel()
    {
        _frameCallback = OnFrameReceived;
        NativeWrapper.set_frame_callback(_frameCallback);
        LoadLocalConnectionSettings();
    }

    private void LoadLocalConnectionSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "connection.local.json");
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var settings = JsonSerializer.Deserialize<LocalConnectionSettings>(File.ReadAllText(settingsPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (settings == null)
        {
            return;
        }

        Host = settings.Host ?? "";
        Domain = settings.Domain ?? "";
        Username = settings.Username ?? "";
        Password = settings.Password ?? "";
        GatewayHost = settings.GatewayHost ?? "";
        GatewayDomain = settings.GatewayDomain ?? "";
        GatewayUsername = settings.GatewayUsername ?? "";
        GatewayPassword = settings.GatewayPassword ?? "";
    }

    [RelayCommand]
    private void Connect()
    {
        int width = _requestedWidth;
        int height = _requestedHeight;

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

    public void UpdateResolution(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _requestedWidth = width;
        _requestedHeight = height;
        NativeWrapper.update_resolution(width, height);
    }

    private void OnFrameReceived(IntPtr data, int width, int height)
    {
        var size = width * height * 4;
        var frame = new byte[size];
        Marshal.Copy(data, frame, 0, size);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Screen == null || Screen.PixelSize.Width != width || Screen.PixelSize.Height != height)
            {
                Console.WriteLine($"[DEBUG_LOG] Resizing Screen to {width}x{height}");
                Screen = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var lockedBitmap = Screen.Lock())
            {
                unsafe
                {
                    fixed (byte* framePtr = frame)
                    {
                        Buffer.MemoryCopy(framePtr, lockedBitmap.Address.ToPointer(), size, size);
                    }
                }
            }

            OnPropertyChanged(nameof(Screen));
            RequestRedraw?.Invoke(this, EventArgs.Empty);
        });
    }

    public event EventHandler? RequestRedraw;

    private sealed class LocalConnectionSettings
    {
        public string? Host { get; set; }
        public string? Domain { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? GatewayHost { get; set; }
        public string? GatewayDomain { get; set; }
        public string? GatewayUsername { get; set; }
        public string? GatewayPassword { get; set; }
    }
}

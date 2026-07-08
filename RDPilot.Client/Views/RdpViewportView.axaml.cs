using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class RdpViewportView : UserControl
{
    private readonly DispatcherTimer _clipboardPollTimer;
    private readonly RdpViewportPresenter _presenter;
    private MainWindowViewModel? _subscribedViewModel;
    private Window? _hostWindow;

    public RdpViewportView()
    {
        InitializeComponent();
        _presenter = new RdpViewportPresenter(
            () => DataContext as MainWindowViewModel,
            () => TopLevel.GetTopLevel(this)?.Clipboard,
            () => RdpScrollViewer.Bounds.Size,
            () => RdpImage.InvalidateVisual(),
            ConvertBitmapToDib,
            new ViewportResolutionService(),
            new ClipboardSyncService());

        _clipboardPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _clipboardPollTimer.Tick += async (_, _) => await _presenter.PollClipboardAsync();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => AttachToHostWindow();
        DetachedFromVisualTree += (_, _) => DetachFromHostWindow();
        RdpImage.LostFocus += (_, _) => _presenter.ReleasePressedRdpKeys();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.SessionRedrawRequested -= OnSessionRedrawRequested;
            _subscribedViewModel.RemoteClipboardTextReceived -= OnRemoteClipboardTextReceived;
        }

        _subscribedViewModel = DataContext as MainWindowViewModel;
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.SessionRedrawRequested += OnSessionRedrawRequested;
            _subscribedViewModel.RemoteClipboardTextReceived += OnRemoteClipboardTextReceived;
            QueueViewportResolutionUpdate();
        }
    }

    private void AttachToHostWindow()
    {
        if (_hostWindow != null)
        {
            return;
        }

        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (_hostWindow == null)
        {
            return;
        }

        _hostWindow.SizeChanged += OnHostWindowSizeChanged;
        _hostWindow.Deactivated += OnHostWindowDeactivated;
        _hostWindow.LayoutUpdated += OnHostWindowLayoutUpdated;
        _hostWindow.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        _hostWindow.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, true);
        _hostWindow.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Bubble, true);
        _clipboardPollTimer.Start();
        QueueViewportResolutionUpdate();
    }

    private void DetachFromHostWindow()
    {
        _clipboardPollTimer.Stop();
        _presenter.ReleasePressedRdpKeys();

        if (_hostWindow == null)
        {
            return;
        }

        _hostWindow.SizeChanged -= OnHostWindowSizeChanged;
        _hostWindow.Deactivated -= OnHostWindowDeactivated;
        _hostWindow.LayoutUpdated -= OnHostWindowLayoutUpdated;
        _hostWindow.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _hostWindow.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        _hostWindow.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        _hostWindow = null;
    }

    private void OnSessionRedrawRequested(object? sender, RdpSessionViewModel e)
    {
        _presenter.HandleSessionRedrawRequested();
    }

    private void OnRemoteClipboardTextReceived(object? sender, (RdpSessionViewModel Session, string Text) e)
    {
        Dispatcher.UIThread.Post(async () => await _presenter.HandleRemoteClipboardTextReceivedAsync(e.Text));
    }

    private static byte[]? ConvertBitmapToDib(Bitmap bitmap)
    {
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            var bytes = stream.ToArray();
            if (bytes.Length <= 14)
            {
                return bytes;
            }

            if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                var dib = new byte[bytes.Length - 14];
                Buffer.BlockCopy(bytes, 14, dib, 0, dib.Length);
                return dib;
            }

            return bytes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIPRDR] failed to encode bitmap clipboard data: {ex.Message}");
            return null;
        }
    }

    private void OnHostWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueViewportResolutionUpdate();
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        _presenter.ReleasePressedRdpKeys();
    }

    private void OnHostWindowLayoutUpdated(object? sender, EventArgs e)
    {
        _presenter.HandleScaleChanged(_hostWindow?.RenderScaling ?? 1.0, _hostWindow?.WindowState == WindowState.Minimized);
    }

    private void OnRdpViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueViewportResolutionUpdate();
    }

    private void QueueViewportResolutionUpdate()
    {
        _presenter.QueueViewportResolutionUpdate(_hostWindow?.RenderScaling ?? 1.0, _hostWindow?.WindowState == WindowState.Minimized);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        _presenter.HandlePointerMoved(pos.X, pos.Y);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        _presenter.HandlePointerPressed(pos.X, pos.Y, e.GetCurrentPoint(RdpImage).Properties);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        _presenter.HandlePointerReleased(pos.X, pos.Y, e.GetCurrentPoint(RdpImage).Properties.PointerUpdateKind);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(RdpImage);
        _presenter.HandlePointerWheelChanged(pos.X, pos.Y, e.Delta);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = _presenter.HandleKeyDown(e.Source, RdpImage, e.Key);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        e.Handled = _presenter.HandleKeyUp(e.Source, RdpImage, e.Key);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        _presenter.HandleTextInput(e.Source, RdpImage, e.Text);
    }
}

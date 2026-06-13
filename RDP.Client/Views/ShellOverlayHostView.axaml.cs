using Avalonia;
using Avalonia.Controls;

namespace RDP.Client.Views;

public partial class ShellOverlayHostView : UserControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ShellOverlayHostView, bool>(nameof(IsOpen));

    public static readonly StyledProperty<double> OverlayWidthProperty =
        AvaloniaProperty.Register<ShellOverlayHostView, double>(nameof(OverlayWidth), 280);

    public static readonly StyledProperty<object?> OverlayContentProperty =
        AvaloniaProperty.Register<ShellOverlayHostView, object?>(nameof(OverlayContent));

    public ShellOverlayHostView()
    {
        InitializeComponent();
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double OverlayWidth
    {
        get => GetValue(OverlayWidthProperty);
        set => SetValue(OverlayWidthProperty, value);
    }

    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }
}

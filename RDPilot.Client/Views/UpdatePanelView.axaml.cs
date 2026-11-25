using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class UpdatePanelView : UserControl
{
    public UpdatePanelView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as AppUpdateViewModel)?.RefreshOpenSessions();
    }

    private void OnLaterClicked(object? sender, RoutedEventArgs e)
    {
        (DataContext as AppUpdateViewModel)?.LaterCommand.Execute(null);
        this.FindLogicalAncestorOfType<Popup>()?.Close();
    }
}

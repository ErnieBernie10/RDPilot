using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RDPilot.Client.Models;
using RDPilot.Client.ViewModels;

namespace RDPilot.Client.Views;

public partial class ConnectionListView : UserControl
{
    public ConnectionListView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void Connect(SavedConnection? connection)
    {
        if (connection != null && ViewModel is { } vm && vm.ConnectToCommand.CanExecute(connection))
        {
            vm.ConnectToCommand.Execute(connection);
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: SavedConnection connection })
        {
            Connect(connection);
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Connect(ConnectionList.SelectedItem as SavedConnection);
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.Enter)
        {
            var target = vm.SelectedConnection is { } selected && vm.FilteredConnections.Contains(selected)
                ? selected
                : vm.FilteredConnections.FirstOrDefault();
            Connect(target);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && vm.FilteredConnections.Count > 0)
        {
            ConnectionList.SelectedIndex = Math.Max(ConnectionList.SelectedIndex, 0);
            ConnectionList.ContainerFromIndex(ConnectionList.SelectedIndex)?.Focus();
            e.Handled = true;
        }
    }

    private void OnRowConnectClicked(object? sender, RoutedEventArgs e)
    {
        Connect((sender as Control)?.DataContext as SavedConnection);
    }

    private async void OnRowEditClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SavedConnection connection && ViewModel is { } vm)
        {
            await ConnectionDialogs.EditAsync(this, vm, connection);
        }
    }

    private async void OnRowDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SavedConnection connection && ViewModel is { } vm)
        {
            await ConnectionDialogs.DeleteAsync(this, vm, connection);
        }
    }

    private async void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await ConnectionDialogs.AddAsync(this, vm);
        }
    }
}

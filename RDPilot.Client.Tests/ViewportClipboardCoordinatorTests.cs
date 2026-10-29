using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.Models;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ViewportClipboardCoordinatorTests : IDisposable
{
    private readonly string? _previousConfigHome = Environment.GetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable);
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), "rdpilot-clipboard-tests-" + Guid.NewGuid().ToString("N"));

    public ViewportClipboardCoordinatorTests()
    {
        Directory.CreateDirectory(_configHome);
        Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _configHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _previousConfigHome);
        Directory.Delete(_configHome, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteFiles_SelectionChangesDuringResolution_DoNotOverwriteClipboard(bool returnToOriginalTab)
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var first = new SessionTestContext("First");
                using var second = new SessionTestContext("Second");
                using var vm = await CreateShellAsync(first.ViewModel);
                await window.Clipboard!.SetTextAsync("keep this");
                using var file = new TemporaryFile();
                var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
                Assert.NotNull(item);
                var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var coordinator = new ViewportClipboardCoordinator(
                    () => vm, () => window.Clipboard, _ => pending.Task, _ => null, new ClipboardSyncService());
                coordinator.Start();

                var update = coordinator.HandleRemoteFilesAsync(first.ViewModel, [item.Path.LocalPath]);
                vm.SelectedSession = second.ViewModel;
                if (returnToOriginalTab) vm.SelectedSession = first.ViewModel;
                // No manual invalidation: this must use the production PropertyChanged subscription.
                pending.SetResult([item]);
                await update;

                Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());
                Assert.Null(await window.Clipboard!.TryGetFilesAsync());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RemoteFiles_CurrentSelection_PublishesNonemptyFiles()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                using var vm = await CreateShellAsync(context.ViewModel);
                using var file = new TemporaryFile();
                var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
                Assert.NotNull(item);
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => Task.FromResult<IStorageItem[]>([item]), _ => null, new ClipboardSyncService());
                coordinator.Start();

                await coordinator.HandleRemoteFilesAsync(context.ViewModel, [item.Path.LocalPath]);

                var files = await window.Clipboard!.TryGetFilesAsync();
                Assert.Equal(item.Name, Assert.Single(files!).Name);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RemoteText_SelectedSession_UpdatesClipboard()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                using var vm = await CreateShellAsync(context.ViewModel);
                using var coordinator = CreateCoordinator(vm, window);
                coordinator.Start();
                await coordinator.HandleRemoteTextAsync(context.ViewModel, "remote clipboard π");
                Assert.Equal("remote clipboard π", await window.Clipboard!.TryGetTextAsync());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PollClipboard_Text_IsSentOnceAndAgainAfterTabChange()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var first = new SessionTestContext("First");
                using var second = new SessionTestContext("Second");
                first.ConnectStatus();
                second.ConnectStatus();
                using var vm = await CreateShellAsync(first.ViewModel);
                using var coordinator = CreateCoordinator(vm, window);
                coordinator.Start();
                await window.Clipboard!.SetTextAsync("local π");

                await coordinator.PollClipboardAsync();
                await coordinator.PollClipboardAsync();
                vm.SelectedSession = second.ViewModel;
                await coordinator.PollClipboardAsync();

                Assert.Equal(["local π"], first.Native.ClipboardTexts);
                Assert.Equal(["local π"], second.Native.ClipboardTexts);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PollClipboard_FilesWithLocalPaths_AreSentToSelectedSession()
    {
        var path = Path.Combine(Path.GetTempPath(), "rdpilot-clipboard-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "clipboard test");
        try
        {
            await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
            {
                var window = new Window();
                window.Show();
                try
                {
                    using var context = new SessionTestContext();
                    context.ConnectStatus();
                    using var vm = await CreateShellAsync(context.ViewModel);
                    using var coordinator = CreateCoordinator(vm, window);
                    coordinator.Start();
                    var item = await window.StorageProvider.TryGetFileFromPathAsync(path);
                    Assert.NotNull(item);
                    await window.Clipboard!.SetFilesAsync([item]);

                    await coordinator.PollClipboardAsync();
                    await coordinator.PollClipboardAsync();

                    Assert.Equal([path], Assert.Single(context.Native.ClipboardFiles));
                    Assert.Empty(context.Native.ClipboardTexts);
                }
                finally { window.Close(); }
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PollClipboard_Bitmap_UsesInjectedEncodingAndForwardsPixels()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                context.ConnectStatus();
                using var vm = await CreateShellAsync(context.ViewModel);
                using var bitmap = new WriteableBitmap(new PixelSize(2, 3), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => Task.FromResult<IStorageItem[]>([]), _ => [1, 2, 3], new ClipboardSyncService());
                coordinator.Start();
                await window.Clipboard!.SetBitmapAsync(bitmap);

                await coordinator.PollClipboardAsync();
                await coordinator.PollClipboardAsync();

                var delivered = Assert.Single(context.Native.ClipboardBitmaps);
                Assert.Equal(new byte[] { 1, 2, 3 }, delivered.Data);
                Assert.Equal(2u, delivered.Width);
                Assert.Equal(3u, delivered.Height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Stop_InvalidatesPendingFilesAndPreventsFurtherPolling()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                context.ConnectStatus();
                using var vm = await CreateShellAsync(context.ViewModel);
                var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var file = new TemporaryFile();
                var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
                Assert.NotNull(item);
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => pending.Task, _ => null, new ClipboardSyncService());
                coordinator.Start();
                await window.Clipboard!.SetTextAsync("unchanged");
                var update = coordinator.HandleRemoteFilesAsync(context.ViewModel, [item.Path.LocalPath]);

                coordinator.Stop();
                pending.SetResult([item]);
                await update;
                await coordinator.PollClipboardAsync();

                Assert.Equal("unchanged", await window.Clipboard!.TryGetTextAsync());
                Assert.Empty(context.Native.ClipboardTexts);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SessionChanged_ReplacementViewModel_UnsubscribesOldShellAndObservesNewSelection()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var first = new SessionTestContext("First");
                using var second = new SessionTestContext("Second");
                using var oldShell = await CreateShellAsync(first.ViewModel);
                using var newShell = await CreateShellAsync(second.ViewModel);
                var currentShell = oldShell;
                using var file = new TemporaryFile();
                var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
                Assert.NotNull(item);
                var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var coordinator = new ViewportClipboardCoordinator(() => currentShell, () => window.Clipboard,
                    _ => pending.Task, _ => null, new ClipboardSyncService());
                coordinator.Start();
                currentShell = newShell;
                // Mirrors the view's DataContextChanged notification, not a tab switch.
                coordinator.SessionChanged();
                var update = coordinator.HandleRemoteFilesAsync(second.ViewModel, [file.Path]);

                oldShell.SelectedSession = null;
                pending.SetResult([item]);
                await update;

                var files = await window.Clipboard!.TryGetFilesAsync();
                Assert.Equal(item.Name, Assert.Single(files!).Name);
                await window.Clipboard!.SetTextAsync("keep this");
                pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
                Assert.NotNull(item);
                update = coordinator.HandleRemoteFilesAsync(second.ViewModel, [file.Path]);
                newShell.SelectedSession = first.ViewModel;
                newShell.SelectedSession = second.ViewModel;
                pending.SetResult([item]);
                await update;

                Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RemoteText_ReachesClipboardOnlyFromSelectedSession()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var first = new SessionTestContext("First");
                using var second = new SessionTestContext("Second", handle: new IntPtr(0x5678));
                using var vm = await CreateShellAsync(first.ViewModel);
                await window.Clipboard!.SetTextAsync("keep this");
                using var coordinator = CreateCoordinator(vm, window);
                coordinator.Start();

                await RaiseRemoteTextAsync(second, "background");
                Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());

                await RaiseRemoteTextAsync(first, "selected");
                Assert.Equal("selected", await window.Clipboard!.TryGetTextAsync());
            }
            finally { window.Close(); }
        });
    }

    private static async Task RaiseRemoteTextAsync(SessionTestContext context, string text)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            context.Callbacks!.ClipboardText(context.Native.Handle, pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static ViewportClipboardCoordinator CreateCoordinator(MainWindowViewModel vm, Window window) =>
        new(() => vm, () => window.Clipboard, _ => Task.FromResult<IStorageItem[]>([]), _ => null, new ClipboardSyncService());

    private static async Task<MainWindowViewModel> CreateShellAsync(RdpSessionViewModel selected)
    {
        var vm = new MainWindowViewModel(new ConnectionStore(new EmptySecretStore()), new NoSessionFactory());
        await vm.LoadConnectionsAsync();
        await Dispatcher.UIThread.InvokeAsync(static () => { });
        vm.SelectedSession = selected;
        return vm;
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public string Description => "Test secrets";
        public Task<string?> GetSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string value) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    }

    private sealed class NoSessionFactory : IRdpSessionFactory
    {
        public RdpSessionViewModel Create(RdpSessionStartRequest request) => throw new NotSupportedException();
    }

    private sealed class TemporaryFile : IDisposable
    {
        internal TemporaryFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpilot-clipboard-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(Path, "clipboard test");
        }
        internal string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}

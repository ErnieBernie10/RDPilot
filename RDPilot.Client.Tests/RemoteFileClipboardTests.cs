using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using RDPilot.Client.Views;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RemoteFileClipboardTests : IDisposable
{
    private readonly string? _previousConfigHome = Environment.GetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable);
    private readonly string _configHome = Path.Combine(Path.GetTempPath(), "rdpilot-remote-files-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rdpilot-remote-files-" + Guid.NewGuid().ToString("N"));

    public RemoteFileClipboardTests()
    {
        Directory.CreateDirectory(_configHome);
        Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _configHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.ConfigHomeEnvironmentVariable, _previousConfigHome);
        Directory.Delete(_configHome, recursive: true);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Runtime_RoutesAnswersByStreamId()
    {
        using var context = new SessionTestContext();
        var first = context.Runtime.RequestFileContentsAsync(5, 0, false, 10, 4, CancellationToken.None);
        var second = context.Runtime.RequestFileContentsAsync(5, 1, true, 0, 0, CancellationToken.None);
        var requests = context.Native.FileContentsRequests;
        Assert.Equal(2, requests.Count);
        Assert.Equal((5u, 0u, false, 10ul, 4u), (requests[0].Generation, requests[0].ListIndex, requests[0].SizeOnly, requests[0].Offset, requests[0].Size));
        Assert.True(requests[1].SizeOnly);
        Assert.NotEqual(requests[0].StreamId, requests[1].StreamId);

        var data = Marshal.AllocCoTaskMem(3);
        try
        {
            Marshal.Copy(new byte[] { 7, 8, 9 }, 0, data, 3);
            context.Callbacks!.ClipboardFileContents(new IntPtr(0x9999), requests[1].StreamId, true, data, 3);
            context.Callbacks.ClipboardFileContents(context.Native.Handle, requests[1].StreamId, true, data, 3);
            context.Callbacks.ClipboardFileContents(context.Native.Handle, requests[0].StreamId, false, IntPtr.Zero, 0);
        }
        finally { Marshal.FreeCoTaskMem(data); }

        Assert.Null(await first);
        Assert.Null(await second);
    }

    [Fact]
    public async Task Runtime_DeliversDataAndFailsWhatIsLeftOnDispose()
    {
        var context = new SessionTestContext();
        var answered = context.Runtime.RequestFileContentsAsync(1, 0, false, 0, 3, CancellationToken.None);
        var pending = context.Runtime.RequestFileContentsAsync(1, 0, false, 3, 3, CancellationToken.None);
        var data = Marshal.AllocCoTaskMem(3);
        try
        {
            Marshal.Copy(new byte[] { 1, 2, 3 }, 0, data, 3);
            context.Callbacks!.ClipboardFileContents(context.Native.Handle, context.Native.FileContentsRequests[0].StreamId, true, data, 3);
        }
        finally { Marshal.FreeCoTaskMem(data); }

        Assert.Equal(new byte[] { 1, 2, 3 }, await answered);
        Assert.False(pending.IsCompleted);
        context.Dispose();
        Assert.Null(await pending);
    }

    [Fact]
    public async Task Runtime_RefusedOrCancelledRequests_CompleteWithNull()
    {
        using var context = new SessionTestContext();
        using var cancellation = new CancellationTokenSource();
        var cancelled = context.Runtime.RequestFileContentsAsync(1, 0, false, 0, 3, cancellation.Token);
        context.Native.AcceptFileContentsRequests = false;
        var refused = context.Runtime.RequestFileContentsAsync(1, 0, false, 0, 3, CancellationToken.None);

        cancellation.Cancel();

        Assert.Null(await refused);
        Assert.Null(await cancelled);
    }

    [Fact]
    public async Task Download_WritesNestedFilesAndReturnsTopLevelItems()
    {
        var source = new FakeRemoteFileSource { Files = { [0] = TestFileDescriptors.Content(700_000), [2] = TestFileDescriptors.Content(10, 3) } };
        var offer = TestFileDescriptors.Offer(source, ("report.txt", 700_000, false), ("folder", null, true), ("folder\\inner.bin", null, false));
        Directory.CreateDirectory(Path.Combine(_root, "previous"));

        var paths = await RemoteFileDownloader.DownloadAsync(offer, _root, CancellationToken.None);

        Assert.Equal(2, paths.Length);
        Assert.Equal("report.txt", Path.GetFileName(paths[0]));
        Assert.Equal("folder", Path.GetFileName(paths[1]));
        Assert.Equal(source.Files[0], await File.ReadAllBytesAsync(paths[0]));
        Assert.Equal(source.Files[2], await File.ReadAllBytesAsync(Path.Combine(paths[1], "inner.bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, "previous")));
    }

    [Fact]
    public async Task Download_Failure_LeavesNoPartialFiles()
    {
        var source = new FakeRemoteFileSource { Files = { [0] = TestFileDescriptors.Content(3 * RemoteFileReader.ChunkSize) }, FailAtOffset = RemoteFileReader.ChunkSize };
        var offer = TestFileDescriptors.Offer(source, ("big.bin", 3 * RemoteFileReader.ChunkSize, false));

        await Assert.ThrowsAsync<IOException>(() => RemoteFileDownloader.DownloadAsync(offer, _root, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("folder\\..\\..\\outside.txt")]
    [InlineData("\\\\")]
    public void SafeSegments_RejectTraversal(string name)
    {
        Assert.Throws<IOException>(() => RemoteFileDownloader.ToSafeSegments(name));
    }

    [Fact]
    public void SafeSegments_ReplaceCharactersLocalFileSystemsReject()
    {
        Assert.Equal(["a_b", "c_d.txt"], RemoteFileDownloader.ToSafeSegments("a:b\\c?d.txt"));
    }

    [Fact]
    public async Task Coordinator_VirtualOffer_SkipsDownloadAndPausesPollingWhileOwned()
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
                var virtualFiles = new FakeRemoteFileClipboard { Accept = true };
                var downloads = 0;
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => Task.FromResult<IStorageItem[]>([]), _ => null, new ClipboardSyncService(), virtualFiles,
                    (_, _) => { downloads++; return Task.FromResult<string[]>([]); });
                coordinator.Start();
                await window.Clipboard!.SetTextAsync("same text");
                await coordinator.PollClipboardAsync();

                var offer = TestFileDescriptors.Offer(("report.txt", 4, false));
                await coordinator.HandleRemoteFileOfferAsync(context.ViewModel, offer);
                virtualFiles.Owns = true;
                await coordinator.PollClipboardAsync();

                Assert.Same(offer, Assert.Single(virtualFiles.Offers));
                Assert.Equal(0, downloads);
                Assert.Equal(["same text"], context.Native.ClipboardTexts);

                // The user copies the same text again locally: it must reach the server, which now holds files.
                virtualFiles.Owns = false;
                await coordinator.PollClipboardAsync();
                Assert.Equal(["same text", "same text"], context.Native.ClipboardTexts);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Coordinator_WithoutVirtualFiles_DownloadsThenPublishesFiles()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                using var vm = await CreateShellAsync(context.ViewModel);
                Directory.CreateDirectory(_root);
                var path = Path.Combine(_root, "report.txt");
                await File.WriteAllTextAsync(path, "data");
                var item = await window.StorageProvider.TryGetFileFromPathAsync(path);
                Assert.NotNull(item);
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => Task.FromResult<IStorageItem[]>([item]), _ => null, new ClipboardSyncService(), null,
                    (_, _) => Task.FromResult<string[]>([path]));
                coordinator.Start();

                await coordinator.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));

                var files = await window.Clipboard!.TryGetFilesAsync();
                Assert.Equal("report.txt", Assert.Single(files!).Name);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Coordinator_RemoteTextDuringDownload_CancelsItAndWins()
    {
        await AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                using var context = new SessionTestContext();
                using var vm = await CreateShellAsync(context.ViewModel);
                var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var coordinator = new ViewportClipboardCoordinator(() => vm, () => window.Clipboard,
                    _ => throw new InvalidOperationException("Cancelled downloads must not publish files."), _ => null, new ClipboardSyncService(), null,
                    async (_, token) =>
                    {
                        started.SetResult(token);
                        await Task.Delay(Timeout.Infinite, token);
                        return [];
                    });
                coordinator.Start();

                var download = coordinator.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("big.iso", 4, false)));
                var token = await started.Task;
                await coordinator.HandleRemoteTextAsync(context.ViewModel, "newer text");
                await download;

                Assert.True(token.IsCancellationRequested);
                Assert.Equal("newer text", await window.Clipboard!.TryGetTextAsync());
            }
            finally { window.Close(); }
        });
    }

    private static async Task<MainWindowViewModel> CreateShellAsync(RdpSessionViewModel selected)
    {
        var vm = new MainWindowViewModel(new ConnectionStore(new EmptySecretStore()), new NoSessionFactory());
        await vm.LoadConnectionsAsync();
        await Dispatcher.UIThread.InvokeAsync(static () => { });
        vm.SelectedSession = selected;
        return vm;
    }

    private sealed class FakeRemoteFileClipboard : IRemoteFileClipboard
    {
        internal bool Accept { get; set; }
        internal bool Owns { get; set; }
        internal System.Collections.Generic.List<RemoteFileOffer> Offers { get; } = [];
        public bool OwnsClipboard => Owns;

        public Task<bool> OfferAsync(RemoteFileOffer offer)
        {
            Offers.Add(offer);
            return Task.FromResult(Accept);
        }

        public void Dispose() { }
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
        public RdpSessionViewModel Create(Models.RdpSessionStartRequest request, Action<RdpSessionViewModel, string> text,
            Action<RdpSessionViewModel, RemoteFileOffer> files) => throw new NotSupportedException();
    }
}

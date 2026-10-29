using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDPilot.Client.Services;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class ClipboardSyncTests
{
    [Fact]
    public Task RemoteText_SelectedSession_UpdatesClipboard() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var sync = CreateSync(window, selection);

        await sync.HandleRemoteTextAsync(context.ViewModel, "remote clipboard π");

        Assert.Equal("remote clipboard π", await window.Clipboard!.TryGetTextAsync());
    });

    [Fact]
    public Task RemoteText_FromSessionEvents_FollowsTheSelectedSessionOnly() => WithWindowAsync(async window =>
    {
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        var selection = new TestSelection { SelectedSession = first.ViewModel };
        using var sync = CreateSync(window, selection);

        RaiseRemoteText(first, "from first");
        RaiseRemoteText(second, "from background");
        await SettleAsync();
        Assert.Equal("from first", await window.Clipboard!.TryGetTextAsync());

        selection.SelectedSession = second.ViewModel;
        RaiseRemoteText(second, "from second");
        RaiseRemoteText(first, "from background");
        await SettleAsync();
        Assert.Equal("from second", await window.Clipboard!.TryGetTextAsync());
    });

    [Fact]
    public Task RemoteText_QueuedBeforeTabChange_IsDropped() => WithWindowAsync(async window =>
    {
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        var selection = new TestSelection { SelectedSession = first.ViewModel };
        using var sync = CreateSync(window, selection);
        await window.Clipboard!.SetTextAsync("keep this");

        RaiseRemoteText(first, "stale");
        selection.SelectedSession = second.ViewModel;
        await SettleAsync();

        Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());
    });

    [Fact]
    public Task RemoteText_IsNotEchoedBackByPolling() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var sync = CreateSync(window, selection);

        await sync.HandleRemoteTextAsync(context.ViewModel, "remote");
        await sync.PollClipboardAsync();

        Assert.Empty(context.Native.ClipboardTexts);
    });

    [Fact]
    public Task EmptyReads_TransientOneIsIgnoredAndTheSecondClearsTheOfferOnce() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var sync = CreateSync(window, selection);
        await sync.HandleRemoteTextAsync(context.ViewModel, "remote");

        await window.Clipboard!.ClearAsync();
        await sync.PollClipboardAsync();
        await window.Clipboard!.SetTextAsync("remote");
        await sync.PollClipboardAsync();
        Assert.Empty(context.Native.ClipboardTexts);

        await window.Clipboard!.ClearAsync();
        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();
        await window.Clipboard!.SetTextAsync("remote");
        await sync.PollClipboardAsync();
        Assert.Equal(["", "remote"], context.Native.ClipboardTexts);
    });

    [Fact]
    public Task PollClipboard_Text_IsSentOnceAndAgainAfterTabChange() => WithWindowAsync(async window =>
    {
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        first.ConnectStatus();
        second.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = first.ViewModel };
        using var sync = CreateSync(window, selection);
        await window.Clipboard!.SetTextAsync("local π");

        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();
        selection.SelectedSession = second.ViewModel;
        await sync.PollClipboardAsync();

        Assert.Equal(["local π"], first.Native.ClipboardTexts);
        Assert.Equal(["local π"], second.Native.ClipboardTexts);
    });

    [Fact]
    public Task PollClipboard_FilesWithLocalPaths_AreSentToSelectedSession() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var sync = CreateSync(window, selection);
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        await window.Clipboard!.SetFilesAsync([item]);

        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();

        Assert.Equal([file.Path], Assert.Single(context.Native.ClipboardFiles));
        Assert.Empty(context.Native.ClipboardTexts);
    });

    [Fact]
    public Task PollClipboard_Bitmap_UsesInjectedEncodingAndForwardsPixels() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var bitmap = new WriteableBitmap(new PixelSize(2, 3), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var sync = CreateSync(window, selection, convertBitmapToDib: _ => [1, 2, 3]);
        await window.Clipboard!.SetBitmapAsync(bitmap);

        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();

        var delivered = Assert.Single(context.Native.ClipboardBitmaps);
        Assert.Equal(new byte[] { 1, 2, 3 }, delivered.Data);
        Assert.Equal(2u, delivered.Width);
        Assert.Equal(3u, delivered.Height);
    });

    [Fact]
    public Task PollClipboard_SameSizeBitmapsWithDifferentContent_AreBothSent() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        using var bitmap = new WriteableBitmap(new PixelSize(2, 3), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        byte[] dib = [1, 2, 3, 4];
        using var sync = CreateSync(window, selection, convertBitmapToDib: _ => dib);
        await window.Clipboard!.SetBitmapAsync(bitmap);

        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();
        dib = [1, 2, 3, 5];
        await sync.PollClipboardAsync();

        Assert.Equal(2, context.Native.ClipboardBitmaps.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, context.Native.ClipboardBitmaps[0].Data);
        Assert.Equal(new byte[] { 1, 2, 3, 5 }, context.Native.ClipboardBitmaps[1].Data);
    });

    [Fact]
    public Task PollClipboard_WhileRemoteFilesArePublished_DoesNotReadTheClipboard() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = CreateSync(window, selection, _ => pending.Task, downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));
        await window.Clipboard!.SetTextAsync("local");

        var update = sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));
        await sync.PollClipboardAsync();
        pending.SetResult([]);
        await update;

        Assert.Empty(context.Native.ClipboardTexts);
    });

    [Fact]
    public Task RemoteFiles_WithoutVirtualFiles_DownloadsThenPublishesFiles() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var context = new SessionTestContext();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        using var sync = CreateSync(window, selection, _ => Task.FromResult<IStorageItem[]>([item]),
            downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));

        await sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));

        var files = await window.Clipboard!.TryGetFilesAsync();
        Assert.Equal(item.Name, Assert.Single(files!).Name);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemoteFiles_SelectionChangesDuringResolution_DoNotOverwriteClipboard(bool returnToOriginalTab) => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        var selection = new TestSelection { SelectedSession = first.ViewModel };
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = CreateSync(window, selection, _ => pending.Task, downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));
        await window.Clipboard!.SetTextAsync("keep this");

        var update = sync.HandleRemoteFileOfferAsync(first.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));
        selection.SelectedSession = second.ViewModel;
        if (returnToOriginalTab) selection.SelectedSession = first.ViewModel;
        pending.SetResult([item]);
        await update;

        Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());
        Assert.Null(await window.Clipboard!.TryGetFilesAsync());
    });

    [Fact]
    public Task RemoteFiles_VirtualOffer_SkipsDownloadAndPausesPollingWhileOwned() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var virtualFiles = new FakeRemoteFileClipboard { Accept = true };
        using var sync = CreateSync(window, selection, remoteFiles: virtualFiles);
        await window.Clipboard!.SetTextAsync("same text");
        await sync.PollClipboardAsync();

        var offer = TestFileDescriptors.Offer(("report.txt", 4, false));
        await sync.HandleRemoteFileOfferAsync(context.ViewModel, offer);
        virtualFiles.Owns = true;
        await sync.PollClipboardAsync();

        Assert.Same(offer, Assert.Single(virtualFiles.Offers));
        Assert.Equal(["same text"], context.Native.ClipboardTexts);

        // The user copies the same text again locally: it must reach the server, which now holds files.
        virtualFiles.Owns = false;
        await sync.PollClipboardAsync();
        Assert.Equal(["same text", "same text"], context.Native.ClipboardTexts);
    });

    [Fact]
    public Task RemoteFiles_RejectedVirtualOffer_FallsBackToDownload() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var context = new SessionTestContext();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        var virtualFiles = new FakeRemoteFileClipboard { Accept = false };
        using var sync = CreateSync(window, selection, _ => Task.FromResult<IStorageItem[]>([item]), remoteFiles: virtualFiles,
            downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));

        await sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));

        Assert.Single(virtualFiles.Offers);
        Assert.Equal(item.Name, Assert.Single((await window.Clipboard!.TryGetFilesAsync())!).Name);
    });

    [Fact]
    public Task Download_RemoteTextArrives_CancelsItAndWins() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var download = new PendingDownload();
        using var sync = CreateSync(window, selection, _ => throw new InvalidOperationException("Cancelled downloads must not publish files."),
            downloadFiles: download.RunAsync);

        var offer = sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("big.iso", 4, false)));
        var token = await download.Started;
        await sync.HandleRemoteTextAsync(context.ViewModel, "newer text");
        await offer;

        Assert.True(token.IsCancellationRequested);
        Assert.Equal("newer text", await window.Clipboard!.TryGetTextAsync());
    });

    [Fact]
    public Task Download_LocalCopy_CancelsItAndReachesTheServer() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var download = new PendingDownload();
        using var sync = CreateSync(window, selection, downloadFiles: download.RunAsync);

        var offer = sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("big.iso", 4, false)));
        var token = await download.Started;
        await window.Clipboard!.SetTextAsync("local copy");
        await sync.PollClipboardAsync();
        await offer;

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(["local copy"], context.Native.ClipboardTexts);
    });

    [Fact]
    public Task Download_EmptyLocalClipboard_IsNotAnnounced() => WithWindowAsync(async window =>
    {
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var download = new PendingDownload();
        using var sync = CreateSync(window, selection, downloadFiles: download.RunAsync);
        await window.Clipboard!.SetTextAsync("local");
        await sync.PollClipboardAsync();

        var offer = sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("big.iso", 4, false)));
        var token = await download.Started;
        await window.Clipboard!.ClearAsync();
        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();
        await sync.PollClipboardAsync();

        Assert.False(token.IsCancellationRequested);
        Assert.Equal(["local"], context.Native.ClipboardTexts);
        selection.SelectedSession = null;
        await offer;
    });

    [Fact]
    public Task Download_TabChange_CancelsIt() => WithWindowAsync(async window =>
    {
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        var selection = new TestSelection { SelectedSession = first.ViewModel };
        var download = new PendingDownload();
        using var sync = CreateSync(window, selection, downloadFiles: download.RunAsync);

        var offer = sync.HandleRemoteFileOfferAsync(first.ViewModel, TestFileDescriptors.Offer(("big.iso", 4, false)));
        var token = await download.Started;
        selection.SelectedSession = second.ViewModel;
        await offer;

        Assert.True(token.IsCancellationRequested);
    });

    [Fact]
    public Task Stop_InvalidatesPendingFilesAndPreventsFurtherPolling() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var context = new SessionTestContext();
        context.ConnectStatus();
        await SettleAsync();
        var selection = new TestSelection { SelectedSession = context.ViewModel };
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = CreateSync(window, selection, _ => pending.Task, downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));
        await window.Clipboard!.SetTextAsync("unchanged");
        var update = sync.HandleRemoteFileOfferAsync(context.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));

        sync.Stop();
        pending.SetResult([item]);
        await update;
        await sync.PollClipboardAsync();

        Assert.Equal("unchanged", await window.Clipboard!.TryGetTextAsync());
        Assert.Empty(context.Native.ClipboardTexts);
    });

    [Fact]
    public Task UseSource_Replacement_UnsubscribesTheOldSourceAndFollowsTheNewOne() => WithWindowAsync(async window =>
    {
        using var file = new TemporaryFile();
        using var first = new SessionTestContext("First");
        using var second = new SessionTestContext("Second");
        var oldSource = new TestSelection { SelectedSession = first.ViewModel };
        var newSource = new TestSelection { SelectedSession = second.ViewModel };
        var item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        var pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = CreateSync(window, oldSource, _ => pending.Task, downloadFiles: (_, _) => Task.FromResult<string[]>([file.Path]));
        sync.UseSource(newSource);

        var update = sync.HandleRemoteFileOfferAsync(second.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));
        oldSource.SelectedSession = null;
        pending.SetResult([item]);
        await update;
        Assert.Equal(item.Name, Assert.Single((await window.Clipboard!.TryGetFilesAsync())!).Name);

        await window.Clipboard!.SetTextAsync("keep this");
        pending = new TaskCompletionSource<IStorageItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        item = await window.StorageProvider.TryGetFileFromPathAsync(file.Path);
        Assert.NotNull(item);
        update = sync.HandleRemoteFileOfferAsync(second.ViewModel, TestFileDescriptors.Offer(("report.txt", 4, false)));
        newSource.SelectedSession = first.ViewModel;
        newSource.SelectedSession = second.ViewModel;
        pending.SetResult([item]);
        await update;

        Assert.Equal("keep this", await window.Clipboard!.TryGetTextAsync());
    });

    private static ClipboardSync CreateSync(Window window, ISelectedSessionSource selection,
        Func<string[], Task<IStorageItem[]>>? createStorageItems = null, Func<Bitmap, byte[]?>? convertBitmapToDib = null,
        IRemoteFileClipboard? remoteFiles = null, Func<RemoteFileOffer, CancellationToken, Task<string[]>>? downloadFiles = null)
    {
        var sync = new ClipboardSync(() => window.Clipboard,
            createStorageItems ?? (_ => Task.FromResult<IStorageItem[]>([])),
            convertBitmapToDib ?? (_ => null),
            remoteFiles,
            downloadFiles ?? ((_, _) => throw new InvalidOperationException("Unexpected download.")));
        sync.UseSource(selection);
        sync.Start();
        return sync;
    }

    private static Task WithWindowAsync(Func<Window, Task> test) => AvaloniaTestEnvironment.RunOnUiThreadAsync(async () =>
    {
        var window = new Window();
        window.Show();
        try { await test(window); }
        finally { window.Close(); }
    });

    private static void RaiseRemoteText(SessionTestContext context, string text) => Task.Run(() =>
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        try { context.Callbacks!.ClipboardText(context.Native.Handle, pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }).Wait();

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++) await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private sealed class TestSelection : ISelectedSessionSource
    {
        private RdpSessionViewModel? _selectedSession;

        public RdpSessionViewModel? SelectedSession
        {
            get => _selectedSession;
            set
            {
                _selectedSession = value;
                SelectedSessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? SelectedSessionChanged;
    }

    private sealed class PendingDownload
    {
        private readonly TaskCompletionSource<CancellationToken> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<CancellationToken> Started => _started.Task;

        internal async Task<string[]> RunAsync(RemoteFileOffer offer, CancellationToken token)
        {
            _started.SetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }
    }

    private sealed class FakeRemoteFileClipboard : IRemoteFileClipboard
    {
        internal bool Accept { get; set; }
        internal bool Owns { get; set; }
        internal List<RemoteFileOffer> Offers { get; } = [];
        public bool OwnsClipboard => Owns;

        public Task<bool> OfferAsync(RemoteFileOffer offer)
        {
            Offers.Add(offer);
            return Task.FromResult(Accept);
        }

        public void Dispose() { }
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

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;
using RDPilot.Client.ViewModels;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RemoteCursorCacheTests
{
    [Fact]
    public void Resolve_SameBitmapIdTwice_PullsPixelsOnlyOnce()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var requests = new List<uint>();
        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) =>
        {
            requests.Add(id);
            return true;
        });

        var (first, second) = AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            var a = cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 7, width: 32, height: 32, hotX: 4, hotY: 5);
            var b = cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 7, width: 32, height: 32, hotX: 4, hotY: 5);
            return (a, b);
        });

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal([7u], requests);

        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);
    }

    [Fact]
    public void Resolve_HiddenAndDefault_NeverPullPixels()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var pullCount = 0;
        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) =>
        {
            pullCount++;
            return true;
        });

        var (hidden, hiddenAgain, standard) = AvaloniaTestEnvironment.RunOnUiThread(() => (
            cache.Resolve(RemoteCursorKind.Hidden, 0, 0, 0, 0, 0),
            cache.Resolve(RemoteCursorKind.Hidden, 0, 0, 0, 0, 0),
            cache.Resolve(RemoteCursorKind.Default, 0, 0, 0, 0, 0)));

        Assert.Equal(0, pullCount);
        Assert.NotNull(hidden);
        // The hidden cursor is reused rather than reallocated on every hide/show cycle.
        Assert.Same(hidden, hiddenAgain);
        Assert.Same(Cursor.Default, standard);

        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);
    }

    [Fact]
    public void Resolve_WhenNativeCopyFails_ReturnsNullAndDoesNotCache()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        // Mirrors the native side having already freed the shape between the callback and the pull.
        var pullCount = 0;
        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) =>
        {
            pullCount++;
            return false;
        });

        var (first, second) = AvaloniaTestEnvironment.RunOnUiThread(() => (
            cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 3, width: 32, height: 32, hotX: 0, hotY: 0),
            cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 3, width: 32, height: 32, hotX: 0, hotY: 0)));

        Assert.Null(first);
        Assert.Null(second);
        // A failed pull must not be cached as a negative result: the shape can come back later.
        Assert.Equal(2, pullCount);

        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);
    }

    [Fact]
    public void Resolve_WithNonPositiveDimensions_ReturnsNullWithoutPulling()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var pullCount = 0;
        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) =>
        {
            pullCount++;
            return true;
        });

        var resolved = AvaloniaTestEnvironment.RunOnUiThread(() =>
            cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 1, width: 0, height: 0, hotX: 0, hotY: 0));

        Assert.Null(resolved);
        Assert.Equal(0, pullCount);

        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);
    }

    [Fact]
    public void Resolve_PastCapacity_EvictsOldestAndRebuildsOnNextRequest()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var requests = new List<uint>();
        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) =>
        {
            requests.Add(id);
            return true;
        });

        // 64 is the cache cap; the 65th insert must push id 1 out.
        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            for (uint id = 1; id <= 65; id++)
            {
                Assert.NotNull(cache.Resolve(RemoteCursorKind.Bitmap, id, 16, 16, 0, 0));
            }
        });

        Assert.Equal(65, requests.Count);

        AvaloniaTestEnvironment.RunOnUiThread(() =>
        {
            // Still cached.
            Assert.NotNull(cache.Resolve(RemoteCursorKind.Bitmap, 65, 16, 16, 0, 0));
            // Evicted, so it has to be pulled again.
            Assert.NotNull(cache.Resolve(RemoteCursorKind.Bitmap, 1, 16, 16, 0, 0));
        });

        Assert.Equal(66, requests.Count);
        Assert.Equal(1u, requests[^1]);

        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);
    }

    [Fact]
    public void Resolve_AfterDispose_ReturnsNull()
    {
        AvaloniaTestEnvironment.EnsureInitialized();

        var cache = new RemoteCursorCache((uint id, IntPtr dest, int stride, int width, int height) => true);
        AvaloniaTestEnvironment.RunOnUiThread(cache.Dispose);

        var resolved = AvaloniaTestEnvironment.RunOnUiThread(() =>
            cache.Resolve(RemoteCursorKind.Bitmap, cursorId: 1, width: 16, height: 16, hotX: 0, hotY: 0));

        Assert.Null(resolved);
    }
}

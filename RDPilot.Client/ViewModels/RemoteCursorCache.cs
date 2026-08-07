using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RDPilot.Client.ViewModels;

/// <summary>
/// Turns the native cursor descriptors reported by <c>CursorCallback</c> into Avalonia
/// <see cref="Cursor"/> instances, caching them by the session-unique id the wrapper assigns.
///
/// Building a <see cref="Cursor"/> creates a platform cursor handle, which is far more expensive
/// than the pixel copy itself, so the cache is what keeps hovering across a toolbar cheap: only the
/// first sighting of a shape touches native memory at all.
///
/// UI-thread only. The pixel pull is injected so tests do not need a native session.
/// </summary>
internal sealed class RemoteCursorCache : IDisposable
{
    /// <summary>Mirrors <c>rdp_session_copy_cursor_image</c>.</summary>
    public delegate bool CopyCursorImageDelegate(uint cursorId, IntPtr dest, int destStride, int destWidth, int destHeight);

    // Matches CURSOR_CACHE_CAPACITY in the wrapper: past this the native side has already dropped
    // the image, so a larger managed cache would only hold cursors that can never be refreshed.
    private const int MaxCachedCursors = 64;

    private readonly CopyCursorImageDelegate _copyCursorImage;
    // The source bitmap is kept beside its cursor purely so both can be released deterministically
    // on eviction; the platform cursor handle itself no longer reads from it.
    private readonly Dictionary<uint, (Cursor Cursor, WriteableBitmap Bitmap)> _cursors = [];
    private readonly Queue<uint> _insertionOrder = new();
    private Cursor? _hiddenCursor;
    private bool _disposed;

    public RemoteCursorCache(CopyCursorImageDelegate copyCursorImage)
    {
        _copyCursorImage = copyCursorImage;
    }

    /// <summary>
    /// Resolves a cursor descriptor. Returns <c>null</c> when the shape could not be produced (the
    /// native side already freed it, or the descriptor is malformed); callers should keep whatever
    /// cursor they are currently showing rather than flashing back to the default arrow.
    /// </summary>
    public Cursor? Resolve(RemoteCursorKind kind, uint cursorId, int width, int height, int hotX, int hotY)
    {
        if (_disposed)
        {
            return null;
        }

        switch (kind)
        {
            case RemoteCursorKind.Hidden:
                return _hiddenCursor ??= new Cursor(StandardCursorType.None);
            case RemoteCursorKind.Default:
                return Cursor.Default;
            case RemoteCursorKind.Bitmap:
                break;
            default:
                return null;
        }

        if (_cursors.TryGetValue(cursorId, out var cached))
        {
            return cached.Cursor;
        }

        var built = BuildCursor(cursorId, width, height, hotX, hotY);
        if (built == null)
        {
            return null;
        }

        _cursors[cursorId] = built.Value;
        _insertionOrder.Enqueue(cursorId);
        TrimToCapacity();
        return built.Value.Cursor;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _cursors.Values)
        {
            entry.Cursor.Dispose();
            entry.Bitmap.Dispose();
        }

        _cursors.Clear();
        _insertionOrder.Clear();
        _hiddenCursor?.Dispose();
        _hiddenCursor = null;
    }

    private (Cursor Cursor, WriteableBitmap Bitmap)? BuildCursor(uint cursorId, int width, int height, int hotX, int hotY)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        WriteableBitmap? bitmap = null;
        try
        {
            // Unpremul, unlike the desktop framebuffer: freerdp_image_copy_from_pointer_data
            // produces straight alpha from the AND/XOR masks.
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            bool copied;
            using (var locked = bitmap.Lock())
            {
                copied = _copyCursorImage(cursorId, locked.Address, locked.RowBytes, width, height);
            }

            if (!copied)
            {
                bitmap.Dispose();
                return null;
            }

            // Hotspots are in remote desktop pixels, which map 1:1 to the bitmap pixels Avalonia
            // expects for PixelPoint, so no render-scaling conversion belongs here.
            var hotspot = new PixelPoint(
                Math.Clamp(hotX, 0, width - 1),
                Math.Clamp(hotY, 0, height - 1));
            return (new Cursor(bitmap, hotspot), bitmap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CURSOR] failed to build cursor {cursorId} ({width}x{height}): {ex.Message}");
            bitmap?.Dispose();
            return null;
        }
    }

    private void TrimToCapacity()
    {
        while (_insertionOrder.Count > MaxCachedCursors)
        {
            var evictedId = _insertionOrder.Dequeue();
            if (_cursors.Remove(evictedId, out var evicted))
            {
                evicted.Cursor.Dispose();
                evicted.Bitmap.Dispose();
            }
        }
    }
}

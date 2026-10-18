using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client;

public interface IRemoteFileContentSource
{
    /// <summary>Completes with the server's answer, or null when the request failed or can no longer be answered.</summary>
    Task<byte[]?> RequestFileContentsAsync(uint generation, uint listIndex, bool sizeOnly, long offset, int size, CancellationToken cancellationToken);
}

public sealed record RemoteFileOffer(RemoteFileList Files, IRemoteFileContentSource Source)
{
    internal RemoteFileReader OpenReader(RemoteFileEntry entry) => new(Source, Files.Generation, (uint)entry.Index, entry.Size);
}

/// <summary>Sequential reader over one remote clipboard file that keeps a window of range requests in flight.</summary>
internal sealed class RemoteFileReader : IDisposable
{
    internal const int ChunkSize = 256 * 1024;
    internal const int Window = 8;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    private readonly IRemoteFileContentSource _source;
    private readonly uint _generation;
    private readonly uint _listIndex;
    private readonly Queue<(long Offset, int Size, Task<byte[]?> Data)> _inFlight = new();
    private CancellationTokenSource _requests = new();
    private long? _length;
    private long _position;
    private long _nextRequest;
    private byte[]? _chunk;
    private int _chunkPosition;

    internal RemoteFileReader(IRemoteFileContentSource source, uint generation, uint listIndex, long? length)
    {
        _source = source;
        _generation = generation;
        _listIndex = listIndex;
        _length = length;
    }

    public long Position => _position;

    public async Task<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        if (_length is { } known) return known;
        var data = await Await(_source.RequestFileContentsAsync(_generation, _listIndex, true, 0, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (data.Length < sizeof(long)) throw new IOException("The remote file size response was malformed.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(data);
        if (length < 0) throw new IOException("The remote file size response was malformed.");
        _length = length;
        return length;
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var length = await GetLengthAsync(cancellationToken).ConfigureAwait(false);
        var total = 0;
        while (buffer.Length > total && _position < length)
        {
            if (_chunk == null || _chunkPosition == _chunk.Length)
            {
                Fill(length);
                var (offset, size, request) = _inFlight.Dequeue();
                var data = await Await(request, cancellationToken).ConfigureAwait(false);
                if (data.Length == 0) throw new IOException("The remote file ended before its reported size.");
                if (data.Length > size) throw new IOException("The remote file returned more data than requested.");
                if (data.Length < size) Restart(offset + data.Length);
                _chunk = data;
                _chunkPosition = 0;
                Fill(length);
            }

            var count = (int)Math.Min(Math.Min(buffer.Length - total, _chunk.Length - _chunkPosition), length - _position);
            _chunk.AsMemory(_chunkPosition, count).CopyTo(buffer[total..]);
            _chunkPosition += count;
            _position += count;
            total += count;
        }

        return total;
    }

    public void Seek(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (position == _position) return;
        Restart(position);
        _chunk = null;
        _position = position;
    }

    private void Fill(long length)
    {
        while (_inFlight.Count < Window && _nextRequest < length)
        {
            var size = (int)Math.Min(ChunkSize, length - _nextRequest);
            _inFlight.Enqueue((_nextRequest, size,
                _source.RequestFileContentsAsync(_generation, _listIndex, false, _nextRequest, size, _requests.Token)));
            _nextRequest += size;
        }
    }

    private void Restart(long nextRequest)
    {
        _requests.Cancel();
        _requests.Dispose();
        _requests = new CancellationTokenSource();
        _inFlight.Clear();
        _nextRequest = nextRequest;
    }

    private static async Task<byte[]> Await(Task<byte[]?> request, CancellationToken cancellationToken)
    {
        byte[]? data;
        try
        {
            data = await request.WaitAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("The remote session did not answer a file read in time.", ex);
        }

        return data ?? throw new IOException("The remote session could not provide the file. It may have been replaced on the remote clipboard.");
    }

    public void Dispose()
    {
        _requests.Cancel();
        _requests.Dispose();
        _inFlight.Clear();
    }
}

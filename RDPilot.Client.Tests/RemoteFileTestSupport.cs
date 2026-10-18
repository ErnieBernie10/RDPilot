using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client.Tests;

internal static class TestFileDescriptors
{
    internal static byte[] Build(params (string Name, long? Size, bool Directory)[] files)
    {
        var bytes = new byte[sizeof(uint) + files.Length * RemoteFileList.DescriptorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var record = bytes.AsSpan(sizeof(uint) + i * RemoteFileList.DescriptorSize, RemoteFileList.DescriptorSize);
            var (name, size, directory) = files[i];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x4u | (size.HasValue ? 0x40u : 0u));
            BinaryPrimitives.WriteUInt32LittleEndian(record[36..], directory ? 0x10u : 0x80u);
            BinaryPrimitives.WriteUInt32LittleEndian(record[64..], (uint)((size ?? 0) >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(record[68..], (uint)(size ?? 0));
            Encoding.Unicode.GetBytes(name).CopyTo(record[72..]);
        }
        return bytes;
    }

    internal static RemoteFileOffer Offer(params (string Name, long? Size, bool Directory)[] files) =>
        new(RemoteFileList.TryParse(1, Build(files))!, new FakeRemoteFileSource());

    internal static RemoteFileOffer Offer(FakeRemoteFileSource source, params (string Name, long? Size, bool Directory)[] files) =>
        new(RemoteFileList.TryParse(1, Build(files))!, source);

    internal static byte[] Content(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 31 + seed) >> 3);
        return bytes;
    }
}

/// <summary>Answers file contents requests from in-memory files on the thread pool, like the RDP thread would.</summary>
internal sealed class FakeRemoteFileSource : IRemoteFileContentSource
{
    private readonly object _lock = new();
    private int _outstanding;

    internal Dictionary<uint, byte[]> Files { get; } = [];
    internal List<(bool SizeOnly, long Offset, int Size)> Requests { get; } = [];
    internal int MaxOutstanding { get; private set; }
    internal Func<long, int, int>? TruncateResponse { get; set; }
    internal long? FailAtOffset { get; set; }

    public Task<byte[]?> RequestFileContentsAsync(uint generation, uint listIndex, bool sizeOnly, long offset, int size, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Requests.Add((sizeOnly, offset, size));
            MaxOutstanding = Math.Max(MaxOutstanding, ++_outstanding);
        }

        return Task.Run(async () =>
        {
            await Task.Delay(1);
            lock (_lock) _outstanding--;
            if (cancellationToken.IsCancellationRequested || !Files.TryGetValue(listIndex, out var file)) return null;
            if (sizeOnly)
            {
                var sizeBytes = new byte[sizeof(long)];
                BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, file.Length);
                return sizeBytes;
            }
            if (offset == FailAtOffset) return null;
            var count = (int)Math.Clamp(file.Length - offset, 0, size);
            if (TruncateResponse != null) count = Math.Min(count, TruncateResponse(offset, count));
            return file.AsSpan((int)offset, count).ToArray();
        });
    }
}

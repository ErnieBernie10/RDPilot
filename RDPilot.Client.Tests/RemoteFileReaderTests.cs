using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RDPilot.Client.Models;
using Xunit;

namespace RDPilot.Client.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit test names use underscores for readability.")]
public sealed class RemoteFileReaderTests
{
    [Fact]
    public void Parse_ReadsNamesSizesAndDirectories()
    {
        var files = RemoteFileList.TryParse(9, TestFileDescriptors.Build(("a π.txt", 5_000_000_000, false), ("dir", null, true), ("dir\\b.bin", null, false)));

        Assert.NotNull(files);
        Assert.Equal(9u, files.Generation);
        Assert.Equal(["a π.txt", "dir", "dir\\b.bin"], files.Entries.Select(entry => entry.RelativePath));
        Assert.Equal([5_000_000_000, null, null], files.Entries.Select(entry => entry.Size));
        Assert.Equal([false, true, false], files.Entries.Select(entry => entry.IsDirectory));
        Assert.Equal([0, 1, 2], files.Entries.Select(entry => entry.Index));
    }

    [Fact]
    public void Parse_RejectsEmptyOrTruncatedDescriptors()
    {
        var descriptor = TestFileDescriptors.Build(("a.txt", 1, false), ("b.txt", 1, false));

        Assert.Null(RemoteFileList.TryParse(1, descriptor.AsSpan(0, descriptor.Length - 1).ToArray()));
        Assert.Null(RemoteFileList.TryParse(1, TestFileDescriptors.Build()));
        Assert.Null(RemoteFileList.TryParse(1, [1, 0]));
    }

    [Fact]
    public async Task ReadAsync_ReturnsExactBytesWithAFullWindowInFlight()
    {
        var content = TestFileDescriptors.Content(3 * RemoteFileReader.ChunkSize * RemoteFileReader.Window + 5);
        var source = new FakeRemoteFileSource { Files = { [0] = content } };
        using var reader = new RemoteFileReader(source, 1, 0, content.Length);

        var read = await ReadAllAsync(reader, 100_000);

        Assert.Equal(content, read);
        Assert.Equal(RemoteFileReader.Window, source.MaxOutstanding);
        Assert.All(source.Requests, request => Assert.InRange(request.Size, 1, RemoteFileReader.ChunkSize));
        Assert.DoesNotContain(source.Requests, request => request.SizeOnly);
    }

    [Fact]
    public async Task ReadAsync_UnknownSize_AsksTheServerFirst()
    {
        var content = TestFileDescriptors.Content(1000);
        var source = new FakeRemoteFileSource { Files = { [0] = content } };
        using var reader = new RemoteFileReader(source, 1, 0, null);

        Assert.Equal(content, await ReadAllAsync(reader, 64));
        Assert.True(source.Requests[0].SizeOnly);
    }

    [Fact]
    public async Task ReadAsync_ShortAnswer_ContinuesFromWhatArrived()
    {
        var content = TestFileDescriptors.Content(5 * RemoteFileReader.ChunkSize);
        var source = new FakeRemoteFileSource
        {
            Files = { [0] = content },
            TruncateResponse = (offset, count) => offset == 0 ? count / 3 : count,
        };
        using var reader = new RemoteFileReader(source, 1, 0, content.Length);

        Assert.Equal(content, await ReadAllAsync(reader, 70_000));
    }

    [Fact]
    public async Task ReadAsync_FailedRequest_ThrowsIOException()
    {
        var content = TestFileDescriptors.Content(4 * RemoteFileReader.ChunkSize);
        var source = new FakeRemoteFileSource { Files = { [0] = content }, FailAtOffset = 2L * RemoteFileReader.ChunkSize };
        using var reader = new RemoteFileReader(source, 1, 0, content.Length);

        await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(reader, 100_000));
    }

    [Fact]
    public async Task Seek_ContinuesFromTheNewPosition()
    {
        var content = TestFileDescriptors.Content(3 * RemoteFileReader.ChunkSize);
        var source = new FakeRemoteFileSource { Files = { [0] = content } };
        using var reader = new RemoteFileReader(source, 1, 0, content.Length);
        var buffer = new byte[10];

        await reader.ReadAsync(buffer);
        reader.Seek(RemoteFileReader.ChunkSize + 7);
        var rest = await ReadAllAsync(reader, 4096);

        Assert.Equal(content.AsSpan(RemoteFileReader.ChunkSize + 7).ToArray(), rest);
    }

    private static async Task<byte[]> ReadAllAsync(RemoteFileReader reader, int bufferSize)
    {
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0) output.Write(buffer, 0, read);
        return output.ToArray();
    }
}

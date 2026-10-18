using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RDPilot.Client.Models;

public sealed record RemoteFileEntry(int Index, string RelativePath, bool IsDirectory, long? Size, DateTimeOffset? LastWriteTime);

/// <summary>A server clipboard file list (FILEGROUPDESCRIPTORW) whose contents are read on demand.</summary>
public sealed class RemoteFileList
{
    internal const int DescriptorSize = 592;
    private const int NameOffset = 72;
    private const int NameChars = 260;
    private const uint FdAttributes = 0x4;
    private const uint FdWriteTime = 0x20;
    private const uint FdFileSize = 0x40;
    private const uint FileAttributeDirectory = 0x10;

    private RemoteFileList(uint generation, byte[] descriptor, IReadOnlyList<RemoteFileEntry> entries)
    {
        Generation = generation;
        Descriptor = descriptor;
        Entries = entries;
    }

    public uint Generation { get; }
    public byte[] Descriptor { get; }
    public IReadOnlyList<RemoteFileEntry> Entries { get; }

    internal static RemoteFileList? TryParse(uint generation, byte[] descriptor)
    {
        if (descriptor.Length < sizeof(uint)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
        if (count == 0 || count > (descriptor.Length - sizeof(uint)) / DescriptorSize) return null;

        var entries = new RemoteFileEntry[count];
        for (var i = 0; i < entries.Length; i++)
        {
            var record = descriptor.AsSpan(sizeof(uint) + i * DescriptorSize, DescriptorSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[36..]);
            var writeTime = BinaryPrimitives.ReadInt64LittleEndian(record[56..]);
            var size = ((long)BinaryPrimitives.ReadUInt32LittleEndian(record[64..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
            var nameChars = MemoryMarshal.Cast<byte, char>(record.Slice(NameOffset, NameChars * sizeof(char)));
            var terminator = nameChars.IndexOf('\0');
            var name = new string(terminator < 0 ? nameChars : nameChars[..terminator]);
            if (name.Length == 0) return null;

            entries[i] = new RemoteFileEntry(
                i,
                name,
                (flags & FdAttributes) != 0 && (attributes & FileAttributeDirectory) != 0,
                (flags & FdFileSize) != 0 && size >= 0 ? size : null,
                (flags & FdWriteTime) != 0 && writeTime > 0 ? DateTimeOffset.FromFileTime(writeTime) : null);
        }

        return new RemoteFileList(generation, descriptor, entries);
    }
}

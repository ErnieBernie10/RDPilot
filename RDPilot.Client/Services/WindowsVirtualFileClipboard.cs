using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

/// <summary>Puts remote clipboard files on the local clipboard without downloading them first.</summary>
internal interface IRemoteFileClipboard : IDisposable
{
    /// <summary>True while the clipboard still holds the last offer, so local polling must not report it back.</summary>
    bool OwnsClipboard { get; }

    Task<bool> OfferAsync(RemoteFileOffer offer);
}

/// <summary>
/// Offers remote files as OLE virtual files (FileGroupDescriptorW + FileContents streams), the way mstsc
/// does, so Explorer pulls the bytes over the clipboard channel only when the user pastes. The data
/// object lives on a dedicated STA thread because stream reads block on network round trips.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVirtualFileClipboard : IRemoteFileClipboard
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _startLock = new();
    private Thread? _thread;
    private uint _threadId;
    private bool _stopping;
    private ClipboardFormats _formats;

    public bool OwnsClipboard
    {
        get
        {
            var threadId = Volatile.Read(ref _threadId);
            if (threadId == 0) return false;
            var owner = NativeMethods.GetClipboardOwner();
            return owner != IntPtr.Zero && NativeMethods.GetWindowThreadProcessId(owner, out _) == threadId;
        }
    }

    public Task<bool> OfferAsync(RemoteFileOffer offer)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!EnsureStarted()) return Task.FromResult(false);
        Post(() => result.TrySetResult(SetClipboard(new VirtualFileDataObject(offer, _formats))));
        return result.Task;
    }

    private bool EnsureStarted()
    {
        lock (_startLock)
        {
            if (_stopping) return false;
            if (_thread != null) return Volatile.Read(ref _threadId) != 0;
            using var started = new ManualResetEventSlim();
            _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "RDPilot clipboard files" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            started.Wait();
            return Volatile.Read(ref _threadId) != 0;
        }
    }

    private void Post(Action action)
    {
        _work.Enqueue(action);
        _wake.Set();
    }

    private void Run(ManualResetEventSlim started)
    {
        if (NativeMethods.OleInitialize(IntPtr.Zero) < 0)
        {
            started.Set();
            return;
        }

        _formats = new ClipboardFormats(
            NativeMethods.RegisterClipboardFormat("FileGroupDescriptorW"),
            NativeMethods.RegisterClipboardFormat("FileContents"),
            NativeMethods.RegisterClipboardFormat("Preferred DropEffect"));
        Volatile.Write(ref _threadId, NativeMethods.GetCurrentThreadId());
        started.Set();

        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!Volatile.Read(ref _stopping))
        {
            _ = NativeMethods.MsgWaitForMultipleObjectsEx(1, handles, NativeMethods.Infinite, NativeMethods.QsAllInput, NativeMethods.MwmoInputAvailable);
            while (_work.TryDequeue(out var action)) action();
            while (NativeMethods.PeekMessage(out var message, IntPtr.Zero, 0, 0, NativeMethods.PmRemove))
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }

        if (OwnsClipboard) _ = NativeMethods.OleSetClipboard(null);
        NativeMethods.OleUninitialize();
    }

    private static bool SetClipboard(IDataObject dataObject)
    {
        // Another application may hold the clipboard open for a moment (clipboard managers, the source app).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OleSetClipboard(dataObject) >= 0) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_startLock)
        {
            if (_stopping) return;
            Volatile.Write(ref _stopping, true);
            thread = _thread;
        }
        _wake.Set();
        if (thread == null || thread.Join(TimeSpan.FromSeconds(2))) _wake.Dispose();
    }

    private readonly record struct ClipboardFormats(uint FileDescriptor, uint FileContents, uint PreferredDropEffect);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class VirtualFileDataObject : IDataObject
    {
        private const int DvEFormatEtc = unchecked((int)0x80040064);
        private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
        private const int DataSSameFormatEtc = 0x00040130;
        private const uint FdProgressUi = 0x4000;
        private const int DropEffectCopy = 1;

        private readonly RemoteFileOffer _offer;
        private readonly ClipboardFormats _formats;
        private readonly byte[] _descriptor;

        internal VirtualFileDataObject(RemoteFileOffer offer, ClipboardFormats formats)
        {
            _offer = offer;
            _formats = formats;
            _descriptor = (byte[])offer.Files.Descriptor.Clone();
            for (var i = 0; i < offer.Files.Entries.Count; i++)
            {
                var flags = sizeof(uint) + i * RemoteFileList.DescriptorSize;
                var value = BitConverter.ToUInt32(_descriptor, flags) | FdProgressUi;
                BitConverter.TryWriteBytes(_descriptor.AsSpan(flags), value);
            }
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            var cf = (ushort)format.cfFormat;
            if (cf == _formats.FileDescriptor && format.tymed.HasFlag(TYMED.TYMED_HGLOBAL))
            {
                medium = CreateHGlobalMedium(_descriptor);
                return;
            }

            if (cf == _formats.PreferredDropEffect && format.tymed.HasFlag(TYMED.TYMED_HGLOBAL))
            {
                medium = CreateHGlobalMedium(BitConverter.GetBytes(DropEffectCopy));
                return;
            }

            if (cf == _formats.FileContents && format.tymed.HasFlag(TYMED.TYMED_ISTREAM) &&
                format.lindex >= 0 && format.lindex < _offer.Files.Entries.Count &&
                !_offer.Files.Entries[format.lindex].IsDirectory)
            {
                var entry = _offer.Files.Entries[format.lindex];
                var stream = new VirtualFileStream(_offer.OpenReader(entry), entry);
                medium = new STGMEDIUM
                {
                    tymed = TYMED.TYMED_ISTREAM,
                    unionmember = Marshal.GetComInterfaceForObject<VirtualFileStream, IStream>(stream),
                };
                return;
            }

            throw ComError(DvEFormatEtc);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();

        public int QueryGetData(ref FORMATETC format)
        {
            var cf = (ushort)format.cfFormat;
            var supported = (cf == _formats.FileDescriptor || cf == _formats.PreferredDropEffect)
                ? format.tymed.HasFlag(TYMED.TYMED_HGLOBAL)
                : cf == _formats.FileContents && format.tymed.HasFlag(TYMED.TYMED_ISTREAM);
            return supported ? 0 : DvEFormatEtc;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = IntPtr.Zero;
            return DataSSameFormatEtc;
        }

        // Explorer reports "Performed DropEffect" / "Paste Succeeded" here; there is nothing to act on.
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            if (release) NativeMethods.ReleaseStgMedium(ref medium);
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) throw new NotImplementedException();
            FORMATETC[] formats =
            [
                Format(_formats.FileDescriptor, TYMED.TYMED_HGLOBAL),
                Format(_formats.FileContents, TYMED.TYMED_ISTREAM),
                Format(_formats.PreferredDropEffect, TYMED.TYMED_HGLOBAL),
            ];
            Marshal.ThrowExceptionForHR(NativeMethods.SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
            return enumerator;
        }

        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return OleEAdviseNotSupported;
        }

        public void DUnadvise(int connection) => throw ComError(OleEAdviseNotSupported);

        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
        {
            enumAdvise = null;
            return OleEAdviseNotSupported;
        }

        private const int EOutOfMemory = unchecked((int)0x8007000E);

        private static FORMATETC Format(uint cf, TYMED tymed) => new()
        {
            cfFormat = unchecked((short)cf),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = tymed,
        };

        private static STGMEDIUM CreateHGlobalMedium(byte[] data)
        {
            var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (UIntPtr)data.Length);
            if (handle == IntPtr.Zero) throw ComError(EOutOfMemory);
            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
                throw ComError(EOutOfMemory);
            }
            Marshal.Copy(data, 0, pointer, data.Length);
            NativeMethods.GlobalUnlock(handle);
            return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class VirtualFileStream(RemoteFileReader reader, RemoteFileEntry entry) : IStream
    {
        private const int StgEInvalidFunction = unchecked((int)0x80030001);
        private const int StgEAccessDenied = unchecked((int)0x80030005);
        private const int StgEReadFault = unchecked((int)0x8003001E);
        private const int StatFlagNoName = 1;

        public void Read(byte[] buffer, int count, IntPtr bytesRead)
        {
            var read = ReadCore(buffer, Math.Min(count, buffer.Length));
            if (bytesRead != IntPtr.Zero) Marshal.WriteInt32(bytesRead, read);
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            var position = origin switch
            {
                0 => offset,
                1 => reader.Position + offset,
                2 => Length() + offset,
                _ => throw ComError(StgEInvalidFunction),
            };
            if (position < 0) throw ComError(StgEInvalidFunction);
            reader.Seek(position);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
        }

        public void Stat(out STATSTG statistics, int flags)
        {
            var lastWrite = entry.LastWriteTime?.ToFileTime() ?? 0;
            statistics = new STATSTG
            {
                pwcsName = (flags & StatFlagNoName) != 0 ? null! : entry.RelativePath,
                type = 2,
                cbSize = Length(),
                mtime = new FILETIME { dwLowDateTime = (int)lastWrite, dwHighDateTime = (int)(lastWrite >> 32) },
            };
        }

        public void CopyTo(IStream target, long count, IntPtr bytesRead, IntPtr bytesWritten)
        {
            var buffer = new byte[RemoteFileReader.ChunkSize];
            long total = 0;
            while (total < count)
            {
                var read = ReadCore(buffer, (int)Math.Min(buffer.Length, count - total));
                if (read == 0) break;
                target.Write(buffer, read, IntPtr.Zero);
                total += read;
            }
            if (bytesRead != IntPtr.Zero) Marshal.WriteInt64(bytesRead, total);
            if (bytesWritten != IntPtr.Zero) Marshal.WriteInt64(bytesWritten, total);
        }

        public void Write(byte[] buffer, int count, IntPtr bytesWritten) => throw ComError(StgEAccessDenied);
        public void SetSize(long newSize) => throw ComError(StgEInvalidFunction);
        public void Commit(int flags) { }
        public void Revert() { }
        public void LockRegion(long offset, long count, int lockType) => throw ComError(StgEInvalidFunction);
        public void UnlockRegion(long offset, long count, int lockType) => throw ComError(StgEInvalidFunction);
        public void Clone(out IStream stream) => throw new NotImplementedException();

        private int ReadCore(byte[] buffer, int count)
        {
            try
            {
                return reader.ReadAsync(buffer.AsMemory(0, count)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Remote clipboard file read failed for {0}: {1}", entry.RelativePath, ex.Message);
                throw ComError(StgEReadFault);
            }
        }

        private long Length()
        {
            try
            {
                return reader.GetLengthAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Remote clipboard file read failed for {0}: {1}", entry.RelativePath, ex.Message);
                throw ComError(StgEReadFault);
            }
        }
    }

    private static Exception ComError(int hresult) => Marshal.GetExceptionForHR(hresult)!;

    private static class NativeMethods
    {
        internal const uint Infinite = 0xFFFFFFFF;
        internal const uint QsAllInput = 0x04FF;
        internal const uint MwmoInputAvailable = 0x0004;
        internal const uint PmRemove = 0x0001;
        internal const uint GmemMoveable = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("ole32.dll")]
        internal static extern int OleInitialize(IntPtr reserved);

        [DllImport("ole32.dll")]
        internal static extern void OleUninitialize();

        [DllImport("ole32.dll")]
        internal static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject? dataObject);

        [DllImport("ole32.dll")]
        internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        [DllImport("shell32.dll")]
        internal static extern int SHCreateStdEnumFmtEtc(uint count, [In] FORMATETC[] formats, out IEnumFORMATETC enumerator);

        [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterClipboardFormat(string name);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        internal static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern IntPtr DispatchMessage(ref Msg message);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalFree(IntPtr memory);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RDPilot.Client.Models;

namespace RDPilot.Client.Services;

public interface IJumpListService
{
    void Refresh(IReadOnlyList<SavedConnection> connections);
}

public sealed class WindowsJumpListService : IJumpListService
{
    [SupportedOSPlatform("windows")]
    public void Refresh(IReadOnlyList<SavedConnection> connections)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var executablePath = GetJumpListExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            var destinationList = (ICustomDestinationList)new CustomDestinationList();
            var objectArrayId = IIDObjectArray;
            destinationList.BeginList(out _, ref objectArrayId, out var removedDestinations);

            var tasks = (IObjectCollection)new EnumerableObjectCollection();
            try
            {
                foreach (var connection in connections)
                {
                    var link = (IShellLinkW)new ShellLink();
                    try
                    {
                        link.SetPath(executablePath);
                        link.SetArguments($"--connect {QuoteArgument(connection.Id)}");
                        link.SetDescription($"Connect to {connection.Name}");
                        link.SetIconLocation(executablePath, 0);
                        var titleKey = PKEYTitle;
                        var title = PROPVARIANT.FromString(connection.Name);
                        try
                        {
                            ((IPropertyStore)link).SetValue(ref titleKey, title);
                        }
                        finally
                        {
                            title.Dispose();
                        }
                        ((IPropertyStore)link).Commit();
                        tasks.AddObject(link);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(link);
                    }
                }

                destinationList.AppendCategory("Saved connections", (IObjectArray)tasks);
                destinationList.CommitList();
            }
            finally
            {
                if (Marshal.IsComObject(removedDestinations)) Marshal.ReleaseComObject(removedDestinations);
                Marshal.ReleaseComObject(tasks);
                Marshal.ReleaseComObject(destinationList);
            }
        }
        catch (Exception)
        {
            // Shell integration is optional and must not affect the client.
        }
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string? GetJumpListExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var appExecutablePath = Path.Combine(AppContext.BaseDirectory, "RDPilot.Client.exe");
        return File.Exists(appExecutablePath) ? appExecutablePath : null;
    }

    private static readonly Guid IIDObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly PROPERTYKEY PKEYTitle = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint maxSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removedDestinations);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray objects);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray objects);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removedDestinations);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        new void GetCount(out uint count);
        new void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathMax, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(IntPtr owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, PROPVARIANT value);
        void Commit();
    }

    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private class CustomDestinationList;

    [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private class EnumerableObjectCollection;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PROPERTYKEY(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT : IDisposable
    {
        [FieldOffset(0)] private ushort _valueType;
        [FieldOffset(8)] private IntPtr _pointerValue;

        public static PROPVARIANT FromString(string value)
        {
            return new PROPVARIANT
            {
                _valueType = 31,
                _pointerValue = Marshal.StringToCoTaskMemUni(value)
            };
        }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(_pointerValue);
            _pointerValue = IntPtr.Zero;
        }
    }
}

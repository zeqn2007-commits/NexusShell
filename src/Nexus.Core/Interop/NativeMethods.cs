using System.Runtime.InteropServices;

namespace Nexus.Core.Interop;

internal static class NativeMethods
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int ErrorCancelled = unchecked((int)0x800704C7);
    public const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    public static readonly Guid ShellItemInterfaceId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public static readonly Guid ShellItemImageFactoryInterfaceId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public static readonly Guid EnumShellItemsInterfaceId = new("70629033-E363-4A28-A567-0DB78006E6D7");
    public static readonly Guid EnumItemsHandlerId = new("94F60519-2850-4924-AA5A-D15E84868039");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHObjectProperties(IntPtr owner, uint type, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? page);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr drop, uint index, [Out] char[]? file, uint size);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHEmptyRecycleBinW(IntPtr owner, [MarshalAs(UnmanagedType.LPWStr)] string? rootPath, uint flags);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object source, out IntPtr idList);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr idList);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHBindToParent(IntPtr idList, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr childIdList);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILFindLastID(IntPtr idList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr bindContext, out IntPtr idList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder folder);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int virtualKey);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool SetWindowSubclass(
        IntPtr window,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> procedure,
        UIntPtr id,
        UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool RemoveWindowSubclass(
        IntPtr window,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> procedure,
        UIntPtr id);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int AssocQueryStringW(int flags, int what, string association, string? extra, [Out] char[]? output, ref uint length);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int PSGetPropertyKeyFromName([MarshalAs(UnmanagedType.LPWStr)] string name, out PropertyKey key);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    public static extern int GetObjectW(IntPtr handle, int size, out BitmapStruct bitmap);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    public static extern nuint GlobalSize(IntPtr memory);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr memory);

    public static string? TakeCoTaskString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    public static IShellItem? TryCreateShellItem(string path)
    {
        var interfaceId = ShellItemInterfaceId;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var item) >= 0
            ? item as IShellItem
            : null;
    }

    public static IShellItem CreateShellItem(string path)
    {
        var interfaceId = ShellItemInterfaceId;
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var item);
        Marshal.ThrowExceptionForHR(result);
        return (IShellItem)item;
    }

    public static string? GetDisplayName(IShellItem item, Sigdn kind) =>
        item.GetDisplayName(kind, out var name) >= 0 ? TakeCoTaskString(name) : null;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ShellExecuteInfo
{
    public int Size;
    public uint Mask;
    public IntPtr Window;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
    [MarshalAs(UnmanagedType.LPWStr)] public string File;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
    public int Show;
    public IntPtr InstanceApp;
    public IntPtr IdList;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
    public IntPtr ClassKey;
    public uint HotKey;
    public IntPtr IconOrMonitor;
    public IntPtr Process;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ShareInfo1
{
    [MarshalAs(UnmanagedType.LPWStr)] public string NetName;
    public uint Type;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Remark;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapStruct
{
    public int Type;
    public int Width;
    public int Height;
    public int WidthBytes;
    public ushort Planes;
    public ushort BitsPixel;
    public IntPtr Bits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}

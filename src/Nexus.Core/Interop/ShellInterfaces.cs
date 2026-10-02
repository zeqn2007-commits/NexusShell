using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Nexus.Core.Interop;

// Windows Shell COM contracts. Method order mirrors the native vtables exactly;
// inherited IUnknown members are implicit. Do not reorder.

internal enum Sigdn : uint
{
    NormalDisplay = 0x00000000,
    ParentRelativeParsing = 0x80018001,
    DesktopAbsoluteParsing = 0x80028000,
    ParentRelativeEditing = 0x80031001,
    DesktopAbsoluteEditing = 0x8004C000,
    FileSysPath = 0x80058000,
    Url = 0x80068000,
    ParentRelativeForAddressBar = 0x8007C001,
    ParentRelative = 0x80080001
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

    [PreserveSig]
    int GetDisplayName(Sigdn name, out IntPtr displayName);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
}

/// <summary>
/// IShellItem2 with the IShellItem slots flattened in. Managed COM interface
/// inheritance would shift the native slots, so they are redeclared here.
/// </summary>
[ComImport]
[Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
{
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

    [PreserveSig]
    int GetDisplayName(Sigdn name, out IntPtr displayName);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);

    [PreserveSig]
    int GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr propertyStore);

    [PreserveSig]
    int GetPropertyStoreWithCreateObject(uint flags, IntPtr createObject, ref Guid interfaceId, out IntPtr propertyStore);

    [PreserveSig]
    int GetPropertyStoreForKeys(IntPtr keys, uint keyCount, uint flags, ref Guid interfaceId, out IntPtr propertyStore);

    [PreserveSig]
    int GetPropertyDescriptionList(ref PropertyKey key, ref Guid interfaceId, out IntPtr list);

    [PreserveSig]
    int Update(IntPtr bindContext);

    [PreserveSig]
    int GetProperty(ref PropertyKey key, IntPtr value);

    [PreserveSig]
    int GetCLSID(ref PropertyKey key, out Guid value);

    [PreserveSig]
    int GetFileTime(ref PropertyKey key, out FILETIME value);

    [PreserveSig]
    int GetInt32(ref PropertyKey key, out int value);

    [PreserveSig]
    int GetString(ref PropertyKey key, out IntPtr value);

    [PreserveSig]
    int GetUInt32(ref PropertyKey key, out uint value);

    [PreserveSig]
    int GetUInt64(ref PropertyKey key, out ulong value);

    [PreserveSig]
    int GetBool(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool value);
}

[ComImport]
[Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems clone);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;
}

[Flags]
internal enum ImageFactoryFlags : uint
{
    ResizeToFit = 0x00,
    BiggerSizeOk = 0x01,
    MemoryOnly = 0x02,
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly = 0x10,
    CropToSquare = 0x20,
    WideThumbnails = 0x40,
    IconBackground = 0x80,
    ScaleUp = 0x100
}

[ComImport]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, ImageFactoryFlags flags, out IntPtr bitmap);
}

[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig]
    int Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);

    [PreserveSig]
    int SetOperationFlags(uint flags);

    [PreserveSig]
    int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    [PreserveSig]
    int SetProgressDialog(IntPtr dialog);

    [PreserveSig]
    int SetProperties(IntPtr properties);

    [PreserveSig]
    int SetOwnerWindow(IntPtr owner);

    [PreserveSig]
    int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);

    [PreserveSig]
    int ApplyPropertiesToItems(IntPtr items);

    [PreserveSig]
    int RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);

    [PreserveSig]
    int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig]
    int MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);

    [PreserveSig]
    int MoveItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destination);

    [PreserveSig]
    int CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);

    [PreserveSig]
    int CopyItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destination);

    [PreserveSig]
    int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, IntPtr sink);

    [PreserveSig]
    int DeleteItems(IntPtr items);

    [PreserveSig]
    int NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IntPtr sink);

    [PreserveSig]
    int PerformOperations();

    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComImport]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int result);

    [PreserveSig]
    int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig]
    int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig]
    int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    [PreserveSig]
    int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);

    [PreserveSig]
    int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated);

    [PreserveSig]
    int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig]
    int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result, IShellItem? newItem);

    [PreserveSig]
    int UpdateProgress(uint workTotal, uint workSoFar);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);

    void GetIDList(out IntPtr idList);

    void SetIDList(IntPtr idList);

    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

    void GetHotkey(out short hotkey);

    void SetHotkey(short hotkey);

    void GetShowCmd(out int showCmd);

    void SetShowCmd(int showCmd);

    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

    void Resolve(IntPtr window, uint flags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(IntPtr owner, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, ref uint eaten, out IntPtr idList, ref uint attributes);

    [PreserveSig]
    int EnumObjects(IntPtr owner, uint flags, out IntPtr enumerator);

    [PreserveSig]
    int BindToObject(IntPtr idList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int BindToStorage(IntPtr idList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);

    [PreserveSig]
    int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int GetAttributesOf(uint count, IntPtr idLists, ref uint attributes);

    [PreserveSig]
    int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] idLists, ref Guid interfaceId, IntPtr reserved, out IntPtr result);

    [PreserveSig]
    int GetDisplayNameOf(IntPtr idList, uint flags, IntPtr name);

    [PreserveSig]
    int SetNameOf(IntPtr owner, IntPtr idList, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newIdList);
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

    [PreserveSig]
    int InvokeCommand(ref ContextMenuInvokeInfo info);

    [PreserveSig]
    int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint maxChars);
}

/// <summary>Adds owner-drawn items and lazily filled submenus ("Отправить", "Открыть с помощью").</summary>
[ComImport]
[Guid("000214F4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig]
    int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

    [PreserveSig]
    int InvokeCommand(ref ContextMenuInvokeInfo info);

    [PreserveSig]
    int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint maxChars);

    [PreserveSig]
    int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
}

[ComImport]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig]
    int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

    [PreserveSig]
    int InvokeCommand(ref ContextMenuInvokeInfo info);

    [PreserveSig]
    int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint maxChars);

    [PreserveSig]
    int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);

    [PreserveSig]
    int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

/// <summary>CMINVOKECOMMANDINFOEX; handlers that only know the short form read the leading fields.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ContextMenuInvokeInfo
{
    public int Size;
    public uint Mask;
    public IntPtr Window;
    public IntPtr Verb;
    public IntPtr Parameters;
    public IntPtr Directory;
    public int Show;
    public uint HotKey;
    public IntPtr Icon;
    public IntPtr Title;
    public IntPtr VerbW;
    public IntPtr ParametersW;
    public IntPtr DirectoryW;
    public IntPtr TitleW;
    public int InvokeX;
    public int InvokeY;
}

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass
{
}

[ComImport]
[Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
internal class FileOperationCoClass
{
}

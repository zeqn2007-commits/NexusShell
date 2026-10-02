using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Core.Interop;
using Nexus.Core.IO;

namespace Nexus.Core.Shell;

/// <summary>
/// The classic Windows context menu ("Показать дополнительные параметры"): the same verbs and
/// third-party entries (archivers, editors, antivirus, Git) that Explorer shows.
/// Must be called on the UI thread; the menu runs a modal loop until the user picks something.
/// </summary>
public static class ShellContextMenu
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;
    private const uint CmfCanRename = 0x10;
    private const uint CmfItemMenu = 0x80;
    private const uint CmfExtendedVerbs = 0x100;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCommand = 0x100;
    private const uint CmicMaskUnicode = 0x4000;
    private const uint CmicMaskAsyncOk = 0x100000;
    private const uint CmicMaskShiftDown = 0x10000000;
    private const uint CmicMaskPointInvoke = 0x20000000;
    private const uint CmicMaskControlDown = 0x40000000;
    private const uint GcsVerbW = 4;
    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmMenuChar = 0x0120;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int ShowNormal = 1;
    private static readonly UIntPtr SubclassId = 0x4E584D;

    // The menu is modal and lives on the UI thread, so one active menu at a time is enough.
    private static IContextMenu2? _activeMenu2;
    private static IContextMenu3? _activeMenu3;

    /// <summary>True when the items can share one menu (Windows builds it from a single folder).</summary>
    public static bool CanShowFor(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.Skip(1).All(path => ParentsMatch(paths[0], path));

    /// <summary>
    /// Shows the menu for items of one folder at a point in physical screen pixels.
    /// <paramref name="tryHandleVerb"/> sees the chosen verb first and returns true when Nexus carried it out itself.
    /// </summary>
    public static void ShowForItems(IntPtr owner, IReadOnlyList<string> paths, int x, int y, bool extended, bool darkTheme, Func<string, bool> tryHandleVerb)
    {
        if (!CanShowFor(paths))
        {
            throw new ArgumentException("Меню Windows строится для элементов одной папки.", nameof(paths));
        }

        var idLists = new List<IntPtr>(paths.Count);
        IShellFolder? parent = null;
        try
        {
            foreach (var path in paths)
            {
                Marshal.ThrowExceptionForHR(NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var idList, 0, out _));
                idLists.Add(idList);
            }

            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(NativeMethods.SHBindToParent(idLists[0], ref folderId, out parent, out _));
            var children = idLists.Select(NativeMethods.ILFindLastID).ToArray();
            var menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out var menu));
            var flags = CmfItemMenu | (extended ? CmfExtendedVerbs : 0) | (paths.Count == 1 ? CmfCanRename : 0);
            Run(owner, menu, flags, x, y, PathHelper.GetParent(paths[0]), darkTheme, tryHandleVerb);
        }
        finally
        {
            NativeMethods.Release(parent);
            idLists.ForEach(NativeMethods.ILFree);
        }
    }

    /// <summary>Shows the folder background menu ("Создать", "Вставить", "Открыть в терминале"…).</summary>
    public static void ShowForFolder(IntPtr owner, string folderPath, int x, int y, bool extended, bool darkTheme, Func<string, bool> tryHandleVerb)
    {
        var idList = IntPtr.Zero;
        IShellFolder? desktop = null;
        IShellFolder? folder = null;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.SHParseDisplayName(folderPath, IntPtr.Zero, out idList, 0, out _));
            Marshal.ThrowExceptionForHR(NativeMethods.SHGetDesktopFolder(out desktop));
            if (Marshal.ReadInt16(idList) == 0)
            {
                // The desktop is the root of the namespace: it is its own folder.
                folder = desktop;
                desktop = null;
            }
            else
            {
                var folderId = typeof(IShellFolder).GUID;
                Marshal.ThrowExceptionForHR(desktop.BindToObject(idList, IntPtr.Zero, ref folderId, out var folderPointer));
                folder = TakeObject<IShellFolder>(folderPointer);
            }

            var menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(folder.CreateViewObject(owner, ref menuId, out var menu));
            Run(owner, menu, extended ? CmfExtendedVerbs : 0, x, y, folderPath, darkTheme, tryHandleVerb);
        }
        finally
        {
            NativeMethods.Release(folder);
            NativeMethods.Release(desktop);
            if (idList != IntPtr.Zero)
            {
                NativeMethods.ILFree(idList);
            }
        }
    }

    private static unsafe void Run(IntPtr owner, IntPtr menuPointer, uint flags, int x, int y, string? directory, bool darkTheme, Func<string, bool> tryHandleVerb)
    {
        var menu = TakeObject<IContextMenu>(menuPointer);
        var popup = IntPtr.Zero;
        var subclassed = false;
        try
        {
            popup = NativeMethods.CreatePopupMenu();
            Marshal.ThrowExceptionForHR(menu.QueryContextMenu(popup, 0, FirstCommand, LastCommand, flags));

            _activeMenu3 = menu as IContextMenu3;
            _activeMenu2 = _activeMenu3 is null ? menu as IContextMenu2 : null;
            subclassed = NativeMethods.SetWindowSubclass(owner, &ForwardMenuMessages, SubclassId, 0);
            MenuTheme.Apply(darkTheme);

            var command = NativeMethods.TrackPopupMenuEx(popup, TpmReturnCommand | TpmRightButton, x, y, owner, IntPtr.Zero);
            if (command < FirstCommand)
            {
                return;
            }

            var offset = command - FirstCommand;
            if (GetVerb(menu, offset) is { Length: > 0 } verb && tryHandleVerb(verb))
            {
                return;
            }

            Invoke(menu, offset, owner, x, y, directory);
        }
        finally
        {
            if (subclassed)
            {
                NativeMethods.RemoveWindowSubclass(owner, &ForwardMenuMessages, SubclassId);
            }

            _activeMenu2 = null;
            _activeMenu3 = null;
            if (popup != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(popup);
            }

            NativeMethods.Release(menu);
        }
    }

    private static void Invoke(IContextMenu menu, uint offset, IntPtr owner, int x, int y, string? directory)
    {
        var directoryPointer = directory is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(directory);
        try
        {
            var info = new ContextMenuInvokeInfo
            {
                Size = Marshal.SizeOf<ContextMenuInvokeInfo>(),
                Mask = CmicMaskUnicode | CmicMaskPointInvoke | CmicMaskAsyncOk
                    | (IsKeyDown(VkShift) ? CmicMaskShiftDown : 0)
                    | (IsKeyDown(VkControl) ? CmicMaskControlDown : 0),
                Window = owner,
                Verb = (IntPtr)offset,
                VerbW = (IntPtr)offset,
                DirectoryW = directoryPointer,
                Show = ShowNormal,
                InvokeX = x,
                InvokeY = y
            };

            var result = menu.InvokeCommand(ref info);
            if (result < 0 && result != NativeMethods.ErrorCancelled)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(directoryPointer);
        }
    }

    private static string? GetVerb(IContextMenu menu, uint offset)
    {
        var buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString(offset, GcsVerbW, IntPtr.Zero, buffer, 260) >= 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Submenus such as "Отправить" and "Открыть с помощью" are filled when they open, and some
    /// items are owner-drawn: the window that owns the menu has to pass those messages on.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr ForwardMenuMessages(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
        {
            try
            {
                if (_activeMenu3 is { } menu3)
                {
                    if (menu3.HandleMenuMsg2(message, wParam, lParam, out var result) >= 0)
                    {
                        return result;
                    }
                }
                else if (_activeMenu2 is { } menu2 && menu2.HandleMenuMsg(message, wParam, lParam) >= 0)
                {
                    return IntPtr.Zero;
                }
            }
            catch (Exception)
            {
                // An exception must not cross into native code, and a broken shell extension
                // must not take the window down with it.
            }
        }

        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private static T TakeObject<T>(IntPtr pointer)
        where T : class
    {
        try
        {
            return (T)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static bool ParentsMatch(string first, string second)
    {
        var a = PathHelper.GetParent(first);
        var b = PathHelper.GetParent(second);
        return a is null ? b is null : b is not null && PathHelper.AreEqual(a, b);
    }

    private static bool IsKeyDown(int virtualKey) => NativeMethods.GetKeyState(virtualKey) < 0;

    /// <summary>
    /// Win32 popup menus follow the app's light/dark mode only after opting in through
    /// uxtheme's SetPreferredAppMode (ordinal 135) and FlushMenuThemes (136), as Explorer does.
    /// </summary>
    private static class MenuTheme
    {
        private const int ForceDark = 2;
        private const int ForceLight = 3;
        private static readonly IntPtr SetPreferredAppMode;
        private static readonly IntPtr FlushMenuThemes;
        private static int _applied = -1;

        static MenuTheme()
        {
            // The ordinals mean something else before Windows 10 1903.
            if (Environment.OSVersion.Version.Build >= 18362 && NativeLibrary.TryLoad("uxtheme.dll", out var uxtheme))
            {
                SetPreferredAppMode = NativeMethods.GetProcAddress(uxtheme, 135);
                FlushMenuThemes = NativeMethods.GetProcAddress(uxtheme, 136);
            }
        }

        public static unsafe void Apply(bool dark)
        {
            var mode = dark ? ForceDark : ForceLight;
            if (mode == _applied || SetPreferredAppMode == IntPtr.Zero || FlushMenuThemes == IntPtr.Zero)
            {
                return;
            }

            ((delegate* unmanaged[Stdcall]<int, int>)SetPreferredAppMode)(mode);
            ((delegate* unmanaged[Stdcall]<void>)FlushMenuThemes)();
            _applied = mode;
        }
    }
}

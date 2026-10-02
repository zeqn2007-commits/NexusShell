using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Nexus.Core.Interop;
using Nexus.Core.Threading;

namespace Nexus.Core.Shell;

public sealed record RecycleBinEntry(
    string Id,
    string Name,
    string? OriginalPath,
    DateTimeOffset? DeletedAt,
    long? Size,
    string TypeName,
    bool IsFolder,
    string? RecycledPath);

/// <summary>
/// The current user's Windows Recycle Bin through the Shell namespace, exactly as
/// Explorer sees it. All calls are serialized on one STA thread.
/// </summary>
public sealed class RecycleBinService : IDisposable
{
    private const string RecycleBinParsingName = "shell:RecycleBinFolder";
    private const uint SfgaoFolder = 0x20000000;
    private const uint CmicMaskNoAsync = 0x00000100;
    private const uint CmicMaskFlagNoUi = 0x00000400;
    private const uint GcsVerbW = 4;
    private const uint ShercbNoConfirmation = 0x1;
    private const uint ShercbNoProgressUi = 0x2;
    private const uint ShercbNoSound = 0x4;

    private readonly StaTaskScheduler _scheduler = new(1, "Nexus Recycle Bin");
    private readonly Lazy<(PropertyKey DeletedFrom, PropertyKey DateDeleted, PropertyKey Size, PropertyKey TypeText)> _keys = new(() => (
        Key("System.Recycle.DeletedFrom"),
        Key("System.Recycle.DateDeleted"),
        Key("System.Size"),
        Key("System.ItemTypeText")));

    public Task<IReadOnlyList<RecycleBinEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        _scheduler.Run<IReadOnlyList<RecycleBinEntry>>(() =>
        {
            var entries = new List<RecycleBinEntry>();
            Enumerate(item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(CreateEntry(item));
                return false;
            });
            return entries.OrderByDescending(entry => entry.DeletedAt).ToArray();
        }, cancellationToken);

    /// <summary>Restores the selected items to their original folders (Explorer's "Restore").</summary>
    public Task RestoreAsync(IReadOnlyCollection<string> ids, IntPtr owner) =>
        _scheduler.Run(() => WithItems(ids, items => InvokeVerb(items, owner, "undelete")));

    /// <summary>
    /// Restores items that were recycled from <paramref name="originalPaths"/> at or after
    /// <paramref name="since"/>. Used to undo a delete made in Nexus.
    /// </summary>
    public Task<int> RestoreRecentlyDeletedAsync(IReadOnlyCollection<string> originalPaths, DateTimeOffset since, IntPtr owner) =>
        _scheduler.Run(() =>
        {
            var wanted = originalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            Enumerate(item =>
            {
                var entry = CreateEntry(item);
                if (entry.OriginalPath is not null && wanted.Contains(entry.OriginalPath)
                    && entry.DeletedAt is { } deleted && deleted >= since.AddSeconds(-5))
                {
                    ids.Add(entry.Id);
                }

                return false;
            });

            if (ids.Count > 0)
            {
                WithItems(ids, items => InvokeVerb(items, owner, "undelete"));
            }

            return ids.Count;
        });

    /// <summary>Irreversible. The caller must have confirmed with the user.</summary>
    public Task DeletePermanentlyAsync(IReadOnlyCollection<string> ids, IntPtr owner) =>
        _scheduler.Run(() => WithItems(ids, items =>
        {
            var operation = (IFileOperation)new FileOperationCoClass();
            try
            {
                // FOF_NOCONFIRMATION: Nexus already asked; FOF_NOERRORUI keeps errors in our UI.
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x0010 | 0x0400));
                if (owner != IntPtr.Zero)
                {
                    operation.SetOwnerWindow(owner);
                }

                foreach (var item in items)
                {
                    Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
                }

                Marshal.ThrowExceptionForHR(operation.PerformOperations());
            }
            finally
            {
                NativeMethods.Release(operation);
            }
        }));

    /// <summary>Empties the whole Recycle Bin. The caller must have confirmed with the user.</summary>
    public Task EmptyAsync() =>
        _scheduler.Run(() =>
        {
            var result = NativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, ShercbNoConfirmation | ShercbNoProgressUi | ShercbNoSound);
            // S_FALSE / E_UNEXPECTED are returned for an already empty bin.
            if (result < 0 && result != unchecked((int)0x8000FFFF))
            {
                Marshal.ThrowExceptionForHR(result);
            }
        });

    public void Dispose() => _scheduler.Dispose();

    private static PropertyKey Key(string name)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.PSGetPropertyKeyFromName(name, out var key));
        return key;
    }

    /// <summary>Visits every item; the visitor returns true to keep (not release) the COM object.</summary>
    private static void Enumerate(Func<IShellItem, bool> visitor)
    {
        var root = NativeMethods.CreateShellItem(RecycleBinParsingName);
        IEnumShellItems? enumerator = null;
        try
        {
            var handler = NativeMethods.EnumItemsHandlerId;
            var interfaceId = NativeMethods.EnumShellItemsInterfaceId;
            Marshal.ThrowExceptionForHR(root.BindToHandler(IntPtr.Zero, ref handler, ref interfaceId, out var pointer));
            try
            {
                enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            while (enumerator.Next(1, out var item, out var fetched) == NativeMethods.S_OK && fetched == 1 && item is not null)
            {
                var keep = false;
                try
                {
                    keep = visitor(item);
                }
                finally
                {
                    if (!keep)
                    {
                        NativeMethods.Release(item);
                    }
                }
            }
        }
        finally
        {
            NativeMethods.Release(enumerator);
            NativeMethods.Release(root);
        }
    }

    private void WithItems(IReadOnlyCollection<string> ids, Action<IReadOnlyList<IShellItem>> action)
    {
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<IShellItem>();
        try
        {
            Enumerate(item =>
            {
                if (wanted.Contains(GetId(item)))
                {
                    found.Add(item);
                    return true;
                }

                return false;
            });

            if (found.Count != wanted.Count)
            {
                throw new FileNotFoundException("Часть элементов уже не находится в Корзине. Обновите список.");
            }

            action(found);
        }
        finally
        {
            foreach (var item in found)
            {
                NativeMethods.Release(item);
            }
        }
    }

    private static string GetId(IShellItem item) =>
        NativeMethods.GetDisplayName(item, Sigdn.FileSysPath)
        ?? NativeMethods.GetDisplayName(item, Sigdn.DesktopAbsoluteParsing)
        ?? string.Empty;

    private RecycleBinEntry CreateEntry(IShellItem item)
    {
        var keys = _keys.Value;

        // The in-folder name; NormalDisplay of a recycled item is its whole original path.
        var name = NativeMethods.GetDisplayName(item, Sigdn.ParentRelative)
            ?? Path.GetFileName(NativeMethods.GetDisplayName(item, Sigdn.NormalDisplay) ?? "?");
        var recycledPath = NativeMethods.GetDisplayName(item, Sigdn.FileSysPath);
        string? deletedFrom = null;
        DateTimeOffset? deletedAt = null;
        long? size = null;
        string? typeText = null;

        if (item is IShellItem2 item2)
        {
            var key = keys.DeletedFrom;
            if (item2.GetString(ref key, out var value) >= 0)
            {
                deletedFrom = NativeMethods.TakeCoTaskString(value);
            }

            key = keys.DateDeleted;
            if (item2.GetFileTime(ref key, out var fileTime) >= 0)
            {
                deletedAt = ToDate(fileTime);
            }

            key = keys.Size;
            if (item2.GetUInt64(ref key, out var bytes) >= 0 && bytes <= long.MaxValue)
            {
                size = (long)bytes;
            }

            key = keys.TypeText;
            if (item2.GetString(ref key, out var type) >= 0)
            {
                typeText = NativeMethods.TakeCoTaskString(type);
            }
        }

        var isFolder = item.GetAttributes(SfgaoFolder, out var attributes) >= 0 && (attributes & SfgaoFolder) != 0
            && !(recycledPath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false);

        // The display name follows Explorer's "hide extensions" option; the $R file keeps
        // the original extension, so restore it to get the real original file name.
        var extension = isFolder ? string.Empty : Path.GetExtension(recycledPath ?? string.Empty);
        if (extension.Length > 0 && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name += extension;
        }

        return new RecycleBinEntry(
            recycledPath ?? NativeMethods.GetDisplayName(item, Sigdn.DesktopAbsoluteParsing) ?? name,
            name,
            deletedFrom is null ? null : Path.Combine(deletedFrom, name),
            deletedAt,
            size,
            typeText ?? (isFolder ? "Папка с файлами" : "Файл"),
            isFolder,
            recycledPath);
    }

    private static DateTimeOffset? ToDate(FILETIME value)
    {
        var ticks = ((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        if (ticks <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void InvokeVerb(IReadOnlyList<IShellItem> items, IntPtr owner, string verb)
    {
        var idLists = new List<IntPtr>();
        IShellFolder? parent = null;
        IContextMenu? menu = null;
        var popup = IntPtr.Zero;
        try
        {
            foreach (var item in items)
            {
                Marshal.ThrowExceptionForHR(NativeMethods.SHGetIDListFromObject(item, out var idList));
                idLists.Add(idList);
            }

            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(NativeMethods.SHBindToParent(idLists[0], ref folderId, out parent, out _));
            var children = idLists.Select(NativeMethods.ILFindLastID).ToArray();
            var menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out var menuPointer));
            try
            {
                menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPointer);
            }
            finally
            {
                Marshal.Release(menuPointer);
            }

            popup = NativeMethods.CreatePopupMenu();
            var queried = menu.QueryContextMenu(popup, 0, 1, 0x7FFF, 0);
            Marshal.ThrowExceptionForHR(queried);
            var offset = FindVerb(menu, queried & 0xFFFF, verb)
                ?? throw new NotSupportedException("Windows не предоставила команду восстановления.");

            var info = new ContextMenuInvokeInfo
            {
                Size = Marshal.SizeOf<ContextMenuInvokeInfo>(),
                Mask = CmicMaskNoAsync | (owner == IntPtr.Zero ? CmicMaskFlagNoUi : 0),
                Window = owner,
                Verb = new IntPtr(offset),
                Show = 1
            };
            Marshal.ThrowExceptionForHR(menu.InvokeCommand(ref info));
        }
        finally
        {
            if (popup != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(popup);
            }

            NativeMethods.Release(menu);
            NativeMethods.Release(parent);
            foreach (var idList in idLists)
            {
                NativeMethods.ILFree(idList);
            }
        }
    }

    private static int? FindVerb(IContextMenu menu, int commandCount, string verb)
    {
        var buffer = Marshal.AllocHGlobal(520);
        try
        {
            for (var offset = 0; offset < commandCount; offset++)
            {
                if (menu.GetCommandString((UIntPtr)(uint)offset, GcsVerbW, IntPtr.Zero, buffer, 260) >= 0
                    && string.Equals(Marshal.PtrToStringUni(buffer), verb, StringComparison.OrdinalIgnoreCase))
                {
                    return offset;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

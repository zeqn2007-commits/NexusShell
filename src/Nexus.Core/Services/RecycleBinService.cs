using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Models;

namespace Nexus.Core.Services;

public sealed class RecycleBinService : IRecycleBinService
{
    private const string RecycleBinParsingName = "shell:RecycleBinFolder";
    private const uint ShellFolderAttributeFolder = 0x20000000;
    private const uint ContextMenuNormal = 0;
    private const uint GetCanonicalVerbAnsi = 0;
    private const uint GetCanonicalVerbUnicode = 4;
    private const uint ContextMenuInvokeNoAsync = 0x00000100;
    private const uint ContextMenuInvokeNoUi = 0x00000400;
    private const int ShowNormal = 1;
    private const int Success = 0;
    private const int NoMoreItems = 1;
    private const uint FileOperationSilent = 0x0004;
    private const uint FileOperationNoConfirmation = 0x0010;
    private const uint FileOperationNoConfirmMakeDirectory = 0x0200;
    private const uint FileOperationNoErrorUi = 0x0400;
    private const uint FileOperationEarlyFailure = 0x00100000;
    private const uint EmptyNoConfirmation = 0x00000001;
    private const uint EmptyNoProgressUi = 0x00000002;
    private const uint EmptyNoSound = 0x00000004;
    private const int CanonicalVerbBufferLength = 260;

    private static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private static readonly Guid EnumShellItemsInterfaceId =
        new("70629033-E363-4A28-A567-0DB78006E6D7");

    private static readonly Guid EnumItemsHandlerId =
        new("94F60519-2850-4924-AA5A-D15E84868039");

    private static readonly Guid ShellFolderInterfaceId =
        new("000214E6-0000-0000-C000-000000000046");

    private static readonly Guid ContextMenuInterfaceId =
        new("000214E4-0000-0000-C000-000000000046");

    private static readonly Guid FileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");

    private static readonly Lazy<PropertyKey> DeletedFromProperty =
        new(() => GetPropertyKey("System.Recycle.DeletedFrom"));

    private static readonly Lazy<PropertyKey> DateDeletedProperty =
        new(() => GetPropertyKey("System.Recycle.DateDeleted"));

    private static readonly Lazy<PropertyKey> SizeProperty =
        new(() => GetPropertyKey("System.Size"));

    private static readonly Lazy<PropertyKey> ItemTypeTextProperty =
        new(() => GetPropertyKey("System.ItemTypeText"));

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<IReadOnlyList<RecycleBinEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => RunInStaAsync(
                () => EnumerateEntries(cancellationToken),
                cancellationToken),
            cancellationToken);
    }

    public Task RestoreAsync(
        IReadOnlyCollection<string> entryIds,
        CancellationToken cancellationToken = default)
    {
        var identifiers = NormalizeIdentifiers(entryIds);
        if (identifiers.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(
            () => RunInStaAsync(
                () =>
                {
                    var items = ResolveItems(identifiers, cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // One context menu for the complete selection mirrors a
                        // multi-select restore in Explorer. It also avoids one
                        // conflict dialog per item and prevents cancellation from
                        // splitting the selection between separate commands.
                        InvokeContextCommand(items, "undelete", "restore");
                    }
                    finally
                    {
                        ReleaseComObjects(items);
                    }
                },
                cancellationToken),
            cancellationToken);
    }

    public Task DeletePermanentlyAsync(
        IReadOnlyCollection<string> entryIds,
        CancellationToken cancellationToken = default)
    {
        var identifiers = NormalizeIdentifiers(entryIds);
        if (identifiers.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(
            () => RunInStaAsync(
                () =>
                {
                    var items = ResolveItems(identifiers, cancellationToken);
                    try
                    {
                        DeleteShellItemsPermanently(items, cancellationToken);
                    }
                    finally
                    {
                        ReleaseComObjects(items);
                    }
                },
                cancellationToken),
            cancellationToken);
    }

    public Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(
            () => RunInStaAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // SHEmptyRecycleBin is atomic from the caller's perspective
                    // and has no cancellation handle. Do not report cancellation
                    // after it has started.
                    var result = SHEmptyRecycleBin(
                        IntPtr.Zero,
                        null,
                        EmptyNoConfirmation
                        | EmptyNoProgressUi
                        | EmptyNoSound);
                    Marshal.ThrowExceptionForHR(result);
                },
                cancellationToken),
            cancellationToken);
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private Task RunSerializedAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunSerializedAsync(
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private static IReadOnlyList<RecycleBinEntry> EnumerateEntries(
        CancellationToken cancellationToken)
    {
        var entries = new List<RecycleBinEntry>();
        EnumerateShellItems(
            cancellationToken,
            item =>
            {
                entries.Add(CreateEntry(item));
                return ShellItemVisitResult.Continue;
            });

        return entries
            .OrderByDescending(entry => entry.DeletedAt)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<IShellItem> ResolveItems(
        IReadOnlyList<string> identifiers,
        CancellationToken cancellationToken)
    {
        var remaining = identifiers.ToHashSet(StringComparer.Ordinal);
        var resolved = new Dictionary<string, IShellItem>(
            identifiers.Count,
            StringComparer.Ordinal);
        try
        {
            // Resolve the whole selection before changing anything. Otherwise a
            // stale identifier late in the list could leave a partially applied
            // permanent-delete request. A single enumeration also avoids an
            // O(selection x bin-size) property scan for multi-selection.
            EnumerateShellItems(
                cancellationToken,
                item =>
                {
                    var identifier = CreateEntryId(item);
                    if (!remaining.Remove(identifier))
                    {
                        return ShellItemVisitResult.Continue;
                    }

                    resolved.Add(identifier, item);
                    return remaining.Count == 0
                        ? ShellItemVisitResult.KeepAndStop
                        : ShellItemVisitResult.KeepAndContinue;
                });

            if (remaining.Count != 0)
            {
                var missingIdentifier = identifiers.First(remaining.Contains);
                throw new FileNotFoundException(
                    "Элемент больше не находится в Корзине.",
                    missingIdentifier);
            }

            return identifiers
                .Select(identifier => resolved[identifier])
                .ToArray();
        }
        catch
        {
            ReleaseComObjects(resolved.Values);
            throw;
        }
    }

    private static void EnumerateShellItems(
        CancellationToken cancellationToken,
        Func<IShellItem, ShellItemVisitResult> visitor)
    {
        IShellItem? root = null;
        IEnumShellItems? enumerator = null;

        try
        {
            var shellItemInterfaceId = ShellItemInterfaceId;
            var result = SHCreateItemFromParsingName(
                RecycleBinParsingName,
                IntPtr.Zero,
                ref shellItemInterfaceId,
                out root);
            Marshal.ThrowExceptionForHR(result);

            var handlerId = EnumItemsHandlerId;
            var enumInterfaceId = EnumShellItemsInterfaceId;
            var enumeratorPointer = IntPtr.Zero;
            try
            {
                result = root.BindToHandler(
                    IntPtr.Zero,
                    ref handlerId,
                    ref enumInterfaceId,
                    out enumeratorPointer);
                Marshal.ThrowExceptionForHR(result);

                enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(
                    enumeratorPointer);
            }
            finally
            {
                if (enumeratorPointer != IntPtr.Zero)
                {
                    Marshal.Release(enumeratorPointer);
                }
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = enumerator.Next(1, out var item, out var fetched);
                if (result < Success)
                {
                    // On a failed Next call no returned pointer is valid.
                    Marshal.ThrowExceptionForHR(result);
                }

                if (fetched == 0 || item is null)
                {
                    ReleaseComObject(item);
                    if (result != NoMoreItems)
                    {
                        throw new InvalidDataException(
                            "Windows Shell вернула некорректный результат перечисления Корзины.");
                    }

                    break;
                }

                var visitResult = ShellItemVisitResult.Continue;
                var visitorCompleted = false;
                try
                {
                    visitResult = visitor(item);
                    visitorCompleted = true;
                }
                finally
                {
                    // Ownership can transfer to the visitor only after it
                    // returned normally. If it throws, release the COM reference.
                    var keepItem = visitorCompleted
                        && visitResult is
                            ShellItemVisitResult.KeepAndContinue
                            or ShellItemVisitResult.KeepAndStop;
                    if (!keepItem)
                    {
                        ReleaseComObject(item);
                    }
                }

                if (visitResult is
                    ShellItemVisitResult.Stop
                    or ShellItemVisitResult.KeepAndStop)
                {
                    break;
                }
            }
        }
        finally
        {
            ReleaseComObject(enumerator);
            ReleaseComObject(root);
        }
    }

    private static RecycleBinEntry CreateEntry(IShellItem item)
    {
        var shellItem2 = item as IShellItem2;
        var name = GetDisplayName(item, ShellDisplayName.NormalDisplay)
            ?? "Неизвестный элемент";
        var deletedFrom = shellItem2 is null
            ? null
            : GetString(shellItem2, DeletedFromProperty.Value);
        var originalPath = CombineOriginalPath(deletedFrom, name);
        var deletedAt = shellItem2 is null
            ? null
            : GetDateTime(shellItem2, DateDeletedProperty.Value);
        var size = shellItem2 is null
            ? null
            : GetSize(shellItem2, SizeProperty.Value);
        var isDirectory = GetIsDirectory(item);
        var displayType = shellItem2 is null
            ? null
            : GetString(shellItem2, ItemTypeTextProperty.Value);
        if (string.IsNullOrWhiteSpace(displayType))
        {
            var extension = Path.GetExtension(name).TrimStart('.');
            displayType = isDirectory
                ? "Папка"
                : string.IsNullOrWhiteSpace(extension)
                    ? "Файл"
                    : $"{extension.ToUpperInvariant()}-файл";
        }

        return new RecycleBinEntry(
            CreateEntryId(item, originalPath, name, deletedAt, size),
            name,
            originalPath,
            deletedAt,
            size,
            displayType ?? string.Empty,
            isDirectory,
            GetDisplayName(item, ShellDisplayName.FileSystemPath));
    }

    private static string CreateEntryId(IShellItem item)
    {
        var name = GetDisplayName(item, ShellDisplayName.NormalDisplay)
            ?? string.Empty;
        var shellItem2 = item as IShellItem2;
        var deletedFrom = shellItem2 is null
            ? null
            : GetString(shellItem2, DeletedFromProperty.Value);
        var originalPath = CombineOriginalPath(deletedFrom, name);
        var deletedAt = shellItem2 is null
            ? null
            : GetDateTime(shellItem2, DateDeletedProperty.Value);
        var size = shellItem2 is null
            ? null
            : GetSize(shellItem2, SizeProperty.Value);
        return CreateEntryId(item, originalPath, name, deletedAt, size);
    }

    private static string CreateEntryId(
        IShellItem item,
        string? originalPath,
        string name,
        DateTimeOffset? deletedAt,
        long? size)
    {
        var identity =
            GetDisplayName(item, ShellDisplayName.DesktopAbsoluteParsing)
            ?? GetDisplayName(item, ShellDisplayName.FileSystemPath)
            ?? string.Join(
                '\0',
                originalPath ?? string.Empty,
                name,
                deletedAt?.UtcDateTime.Ticks.ToString() ?? string.Empty,
                size?.ToString() ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"recycle:{Convert.ToHexString(hash)}";
    }

    private static string? GetDisplayName(
        IShellItem item,
        ShellDisplayName displayName)
    {
        var result = item.GetDisplayName(displayName, out var namePointer);
        try
        {
            return result >= Success && namePointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePointer)
                : null;
        }
        finally
        {
            if (namePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(namePointer);
            }
        }
    }

    private static string? GetString(
        IShellItem2 item,
        PropertyKey propertyKey)
    {
        var result = item.GetString(ref propertyKey, out var valuePointer);
        try
        {
            return result >= Success && valuePointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(valuePointer)
                : null;
        }
        finally
        {
            if (valuePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(valuePointer);
            }
        }
    }

    private static DateTimeOffset? GetDateTime(
        IShellItem2 item,
        PropertyKey propertyKey)
    {
        var result = item.GetFileTime(ref propertyKey, out var fileTime);
        if (result < 0)
        {
            return null;
        }

        var unsignedValue = ((ulong)(uint)fileTime.dwHighDateTime << 32)
            | (uint)fileTime.dwLowDateTime;
        if (unsignedValue == 0 || unsignedValue > long.MaxValue)
        {
            return null;
        }

        var value = (long)unsignedValue;

        try
        {
            return new DateTimeOffset(
                DateTime.FromFileTimeUtc(value),
                TimeSpan.Zero).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? GetSize(
        IShellItem2 item,
        PropertyKey propertyKey)
    {
        var result = item.GetUInt64(ref propertyKey, out var value);
        return result >= 0 && value <= long.MaxValue
            ? (long)value
            : null;
    }

    private static bool GetIsDirectory(IShellItem item)
    {
        var result = item.GetAttributes(
            ShellFolderAttributeFolder,
            out var attributes);
        return result >= 0
            && (attributes & ShellFolderAttributeFolder) != 0;
    }

    private static string? CombineOriginalPath(
        string? deletedFrom,
        string name)
    {
        if (string.IsNullOrWhiteSpace(deletedFrom))
        {
            return null;
        }

        try
        {
            return Path.Combine(deletedFrom, name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void InvokeContextCommand(
        IReadOnlyList<IShellItem> items,
        params string[] acceptedCanonicalVerbs)
    {
        if (items.Count == 0)
        {
            return;
        }

        var itemIdLists = new List<IntPtr>(items.Count);
        IntPtr contextMenuPointer = IntPtr.Zero;
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        var menu = IntPtr.Zero;

        try
        {
            foreach (var item in items)
            {
                var itemResult = SHGetIDListFromObject(
                    item,
                    out var itemIdList);
                Marshal.ThrowExceptionForHR(itemResult);
                if (itemIdList == IntPtr.Zero)
                {
                    throw new InvalidDataException(
                        "Windows Shell не вернула идентификатор элемента Корзины.");
                }

                itemIdLists.Add(itemIdList);
            }

            var shellFolderInterfaceId = ShellFolderInterfaceId;
            var result = SHBindToParent(
                itemIdLists[0],
                ref shellFolderInterfaceId,
                out parentFolder,
                out _);
            Marshal.ThrowExceptionForHR(result);

            var childItemIdLists = itemIdLists
                .Select(ILFindLastID)
                .ToArray();
            if (childItemIdLists.Any(pointer => pointer == IntPtr.Zero))
            {
                throw new InvalidDataException(
                    "Windows Shell не вернула дочерний идентификатор элемента Корзины.");
            }

            var contextMenuInterfaceId = ContextMenuInterfaceId;
            result = parentFolder.GetUIObjectOf(
                IntPtr.Zero,
                (uint)childItemIdLists.Length,
                childItemIdLists,
                ref contextMenuInterfaceId,
                IntPtr.Zero,
                out contextMenuPointer);
            Marshal.ThrowExceptionForHR(result);

            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(
                contextMenuPointer);
            Marshal.Release(contextMenuPointer);
            contextMenuPointer = IntPtr.Zero;

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Windows не создала контекстное меню Корзины.");
            }

            result = contextMenu.QueryContextMenu(
                menu,
                0,
                1,
                0x7FFF,
                ContextMenuNormal);
            Marshal.ThrowExceptionForHR(result);
            var commandCount = result & 0xFFFF;
            var commandOffset = FindCommandOffset(
                contextMenu,
                commandCount,
                acceptedCanonicalVerbs);
            if (commandOffset is null)
            {
                throw new NotSupportedException(
                    "Windows Shell не предоставила команду восстановления.");
            }

            var command = new ContextMenuInvokeCommandInfo
            {
                Size = Marshal.SizeOf<ContextMenuInvokeCommandInfo>(),
                Mask = ContextMenuInvokeNoAsync | ContextMenuInvokeNoUi,
                Window = IntPtr.Zero,
                Verb = new IntPtr(commandOffset.Value),
                Parameters = IntPtr.Zero,
                Directory = IntPtr.Zero,
                Show = ShowNormal,
                HotKey = 0,
                Icon = IntPtr.Zero
            };

            result = contextMenu.InvokeCommand(ref command);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            if (menu != IntPtr.Zero)
            {
                DestroyMenu(menu);
            }

            ReleaseComObject(contextMenu);
            if (contextMenuPointer != IntPtr.Zero)
            {
                Marshal.Release(contextMenuPointer);
            }

            ReleaseComObject(parentFolder);
            foreach (var itemIdList in itemIdLists)
            {
                Marshal.FreeCoTaskMem(itemIdList);
            }
        }
    }

    private static int? FindCommandOffset(
        IContextMenu contextMenu,
        int commandCount,
        IReadOnlyCollection<string> acceptedCanonicalVerbs)
    {
        for (var commandOffset = 0; commandOffset < commandCount; commandOffset++)
        {
            var verb = GetCanonicalVerb(
                contextMenu,
                commandOffset,
                unicode: true)
                ?? GetCanonicalVerb(
                    contextMenu,
                    commandOffset,
                    unicode: false);
            if (verb is not null
                && acceptedCanonicalVerbs.Contains(
                    verb,
                    StringComparer.OrdinalIgnoreCase))
            {
                return commandOffset;
            }
        }

        return null;
    }

    private static string? GetCanonicalVerb(
        IContextMenu contextMenu,
        int commandOffset,
        bool unicode)
    {
        var characterSize = unicode ? sizeof(char) : sizeof(byte);
        var buffer = Marshal.AllocHGlobal(
            CanonicalVerbBufferLength * characterSize);

        try
        {
            var result = contextMenu.GetCommandString(
                (UIntPtr)(uint)commandOffset,
                unicode ? GetCanonicalVerbUnicode : GetCanonicalVerbAnsi,
                IntPtr.Zero,
                buffer,
                CanonicalVerbBufferLength);
            if (result < 0)
            {
                return null;
            }

            return unicode
                ? Marshal.PtrToStringUni(buffer)
                : Marshal.PtrToStringAnsi(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void DeleteShellItemsPermanently(
        IReadOnlyList<IShellItem> items,
        CancellationToken cancellationToken)
    {
        object? operationObject = null;
        try
        {
            var operationType = Type.GetTypeFromCLSID(
                FileOperationClassId,
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Windows File Operation недоступен.");
            operationObject = Activator.CreateInstance(operationType)
                ?? throw new InvalidOperationException(
                    "Windows File Operation не удалось создать.");
            var operation = (IFileOperation)operationObject;

            var result = operation.SetOperationFlags(
                FileOperationSilent
                | FileOperationNoConfirmation
                | FileOperationNoConfirmMakeDirectory
                | FileOperationNoErrorUi
                | FileOperationEarlyFailure);
            Marshal.ThrowExceptionForHR(result);

            foreach (var item in items)
            {
                result = operation.DeleteItem(item, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(result);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // IFileOperation does not expose a cancellation handle without a
            // progress sink; once PerformOperations starts, it must finish.
            result = operation.PerformOperations();
            Marshal.ThrowExceptionForHR(result);

            result = operation.GetAnyOperationsAborted(out var aborted);
            Marshal.ThrowExceptionForHR(result);
            if (aborted)
            {
                throw new IOException(
                    "Windows не завершила безвозвратное удаление.");
            }
        }
        finally
        {
            ReleaseComObject(operationObject);
        }
    }

    private static IReadOnlyList<string> NormalizeIdentifiers(
        IReadOnlyCollection<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Any(identifier => !IsValidIdentifier(identifier)))
        {
            throw new ArgumentException(
                "Идентификатор элемента Корзины имеет неверный формат.",
                nameof(entryIds));
        }

        return entryIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsValidIdentifier(string? identifier)
    {
        const string prefix = "recycle:";
        const int sha256HexLength = 64;
        if (identifier is null
            || !identifier.StartsWith(prefix, StringComparison.Ordinal)
            || identifier.Length != prefix.Length + sha256HexLength)
        {
            return false;
        }

        return identifier.AsSpan(prefix.Length).IndexOfAnyExcept(
            "0123456789ABCDEF") < 0;
    }

    private static PropertyKey GetPropertyKey(string canonicalName)
    {
        var result = PSGetPropertyKeyFromName(
            canonicalName,
            out var propertyKey);
        Marshal.ThrowExceptionForHR(result);
        return propertyKey;
    }

    private static Task<T> RunInStaAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromException<T>(
                new PlatformNotSupportedException(
                    "Корзина Nexus доступна только в Windows."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(operation());
                }
                catch (OperationCanceledException exception)
                {
                    var token = exception.CancellationToken.CanBeCanceled
                        ? exception.CancellationToken
                        : cancellationToken;
                    completion.TrySetCanceled(token);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            })
        {
            IsBackground = true,
            Name = "Nexus Recycle Bin STA"
        };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private static Task RunInStaAsync(
        Action operation,
        CancellationToken cancellationToken)
    {
        return RunInStaAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ReleaseComObjects(IEnumerable<object> values)
    {
        foreach (var value in values)
        {
            ReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject(
        [MarshalAs(UnmanagedType.IUnknown)] object source,
        out IntPtr itemIdList);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(
        IntPtr itemIdList,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder parentFolder,
        out IntPtr childItemIdList);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern IntPtr ILFindLastID(IntPtr itemIdList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHEmptyRecycleBin(
        IntPtr ownerWindow,
        [MarshalAs(UnmanagedType.LPWStr)] string? rootPath,
        uint flags);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int PSGetPropertyKeyFromName(
        [MarshalAs(UnmanagedType.LPWStr)] string canonicalName,
        out PropertyKey propertyKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(
            IntPtr bindingContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(
            ShellDisplayName displayName,
            out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(
            [MarshalAs(UnmanagedType.Interface)] IShellItem other,
            uint hint,
            out int order);
    }

    [ComImport]
    [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // Keep the inherited IShellItem slots flattened into this declaration.
        // Managed COM interface inheritance can otherwise shift IShellItem2's
        // native vtable slots and turn property reads into invalid calls.
        [PreserveSig]
        int BindToHandler(
            IntPtr bindingContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int GetParent(
            [MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(
            ShellDisplayName displayName,
            out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(
            [MarshalAs(UnmanagedType.Interface)] IShellItem other,
            uint hint,
            out int order);

        [PreserveSig]
        int GetPropertyStore(
            uint flags,
            ref Guid interfaceId,
            out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyStoreWithCreateObject(
            uint flags,
            [MarshalAs(UnmanagedType.IUnknown)] object createObject,
            ref Guid interfaceId,
            out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyStoreForKeys(
            IntPtr keys,
            uint keyCount,
            uint flags,
            ref Guid interfaceId,
            out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyDescriptionList(
            ref PropertyKey propertyKey,
            ref Guid interfaceId,
            out IntPtr propertyDescriptionList);

        [PreserveSig]
        int Update(IntPtr bindingContext);

        [PreserveSig]
        int GetProperty(ref PropertyKey propertyKey, IntPtr value);

        [PreserveSig]
        int GetCLSID(ref PropertyKey propertyKey, out Guid value);

        [PreserveSig]
        int GetFileTime(ref PropertyKey propertyKey, out FILETIME value);

        [PreserveSig]
        int GetInt32(ref PropertyKey propertyKey, out int value);

        [PreserveSig]
        int GetString(ref PropertyKey propertyKey, out IntPtr value);

        [PreserveSig]
        int GetUInt32(ref PropertyKey propertyKey, out uint value);

        [PreserveSig]
        int GetUInt64(ref PropertyKey propertyKey, out ulong value);

        [PreserveSig]
        int GetBool(
            ref PropertyKey propertyKey,
            [MarshalAs(UnmanagedType.Bool)] out bool value);
    }

    [ComImport]
    [Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(
            uint count,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem? item,
            out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(
            [MarshalAs(UnmanagedType.Interface)] out IEnumShellItems clone);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr ownerWindow,
            IntPtr bindingContext,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref uint eaten,
            out IntPtr itemIdList,
            ref uint attributes);

        [PreserveSig]
        int EnumObjects(
            IntPtr ownerWindow,
            uint flags,
            out IntPtr enumerator);

        [PreserveSig]
        int BindToObject(
            IntPtr itemIdList,
            IntPtr bindingContext,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int BindToStorage(
            IntPtr itemIdList,
            IntPtr bindingContext,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int CompareIds(
            IntPtr parameter,
            IntPtr firstItemIdList,
            IntPtr secondItemIdList);

        [PreserveSig]
        int CreateViewObject(
            IntPtr ownerWindow,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int GetAttributesOf(
            uint count,
            IntPtr itemIdLists,
            ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr ownerWindow,
            uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
            IntPtr[] itemIdLists,
            ref Guid interfaceId,
            IntPtr reserved,
            out IntPtr result);

        [PreserveSig]
        int GetDisplayNameOf(
            IntPtr itemIdList,
            uint flags,
            IntPtr name);

        [PreserveSig]
        int SetNameOf(
            IntPtr ownerWindow,
            IntPtr itemIdList,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags,
            out IntPtr renamedItemIdList);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(
            IntPtr menu,
            uint indexMenu,
            uint firstCommandId,
            uint lastCommandId,
            uint flags);

        [PreserveSig]
        int InvokeCommand(ref ContextMenuInvokeCommandInfo commandInfo);

        [PreserveSig]
        int GetCommandString(
            UIntPtr commandOffset,
            uint type,
            IntPtr reserved,
            IntPtr name,
            uint maximumCharacters);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig]
        int Advise(IntPtr progressSink, out uint cookie);

        [PreserveSig]
        int Unadvise(uint cookie);

        [PreserveSig]
        int SetOperationFlags(uint flags);

        [PreserveSig]
        int SetProgressMessage(
            [MarshalAs(UnmanagedType.LPWStr)] string message);

        [PreserveSig]
        int SetProgressDialog(IntPtr progressDialog);

        [PreserveSig]
        int SetProperties(IntPtr propertyChangeArray);

        [PreserveSig]
        int SetOwnerWindow(IntPtr ownerWindow);

        [PreserveSig]
        int ApplyPropertiesToItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item);

        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr items);

        [PreserveSig]
        int RenameItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);

        [PreserveSig]
        int RenameItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string newName);

        [PreserveSig]
        int MoveItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);

        [PreserveSig]
        int MoveItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);

        [PreserveSig]
        int CopyItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);

        [PreserveSig]
        int CopyItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);

        [PreserveSig]
        int DeleteItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            IntPtr progressSink);

        [PreserveSig]
        int DeleteItems(IntPtr items);

        [PreserveSig]
        int NewItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            uint attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName,
            IntPtr progressSink);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted(
            [MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextMenuInvokeCommandInfo
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
    }

    private enum ShellDisplayName : uint
    {
        NormalDisplay = 0x00000000,
        DesktopAbsoluteParsing = 0x80028000,
        FileSystemPath = 0x80058000
    }

    private enum ShellItemVisitResult
    {
        Continue,
        Stop,
        KeepAndContinue,
        KeepAndStop
    }
}

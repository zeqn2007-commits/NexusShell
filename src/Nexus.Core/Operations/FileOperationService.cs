using System.Runtime.InteropServices;
using Nexus.Core.Interop;
using Nexus.Core.IO;
using Nexus.Core.Threading;

namespace Nexus.Core.Operations;

public enum FileOperationKind
{
    Copy,
    Move,
    Recycle,
    DeletePermanently
}

/// <summary>Result of a shell file operation.</summary>
/// <param name="Completed">All requested items were processed.</param>
/// <param name="Cancelled">The user cancelled or skipped part of the operation in a Windows dialog.</param>
/// <param name="CreatedPaths">Items created at the destination (after "keep both" renames).</param>
/// <param name="Error">A message when Windows reported a failure.</param>
public sealed record FileOperationOutcome(
    bool Completed,
    bool Cancelled,
    IReadOnlyList<string> CreatedPaths,
    string? Error = null);

/// <summary>
/// Copy, move and recycle through IFileOperation — the File Explorer engine.
/// Windows provides the progress window, pause/cancel, conflict resolution,
/// elevation prompts and an undo record; nothing is deleted permanently
/// without Windows asking the user first.
/// </summary>
public sealed class FileOperationService
{
    // FOF_* / FOFX_* flags (shobjidl.h)
    private const uint FofSilent = 0x0004;
    private const uint FofRenameOnCollision = 0x0008;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxShowElevationPrompt = 0x00040000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxEarlyFailure = 0x00100000;
    private const uint FofxAddUndoRecord = 0x20000000;

    /// <summary>When true, no Windows UI is shown (tests and background work in owned temp folders).</summary>
    public bool Silent { get; init; }

    public Task<FileOperationOutcome> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IntPtr owner) =>
        RunAsync(FileOperationKind.Copy, sources, destinationFolder, owner);

    public Task<FileOperationOutcome> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IntPtr owner) =>
        RunAsync(FileOperationKind.Move, sources, destinationFolder, owner);

    /// <summary>
    /// Sends items to the Recycle Bin. If an item cannot be recycled (for example on
    /// a network share), Windows shows its own "delete permanently?" warning.
    /// </summary>
    public Task<FileOperationOutcome> RecycleAsync(IReadOnlyList<string> paths, IntPtr owner) =>
        RunAsync(FileOperationKind.Recycle, paths, null, owner);

    /// <summary>Shift+Delete: Windows itself asks "Удалить безвозвратно?" before anything is destroyed.</summary>
    public Task<FileOperationOutcome> DeletePermanentlyAsync(IReadOnlyList<string> paths, IntPtr owner) =>
        Silent
            ? Task.FromResult(new FileOperationOutcome(false, false, [], "Безвозвратное удаление без подтверждения запрещено."))
            : RunAsync(FileOperationKind.DeletePermanently, paths, null, owner);

    public static string CreateFolder(string parent, string name)
    {
        var error = FileNameValidator.Validate(name);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(name));
        }

        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"«{name}» уже существует в этой папке.");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    public static string CreateEmptyFile(string parent, string name)
    {
        var error = FileNameValidator.Validate(name);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(name));
        }

        var path = Path.Combine(parent, name);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        return path;
    }

    /// <summary>Renames in place. Changing only the letter case is supported.</summary>
    public static string Rename(string path, string newName)
    {
        var error = FileNameValidator.Validate(newName);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(newName));
        }

        var parent = PathHelper.GetParent(path) ?? throw new IOException("Корень диска нельзя переименовать здесь.");
        var destination = Path.Combine(parent, newName);
        if (string.Equals(path, destination, StringComparison.Ordinal))
        {
            return path;
        }

        var caseOnly = string.Equals(path, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new IOException($"«{newName}» уже существует в этой папке.");
        }

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new FileNotFoundException("Элемент больше не существует.", path);
        }

        if (isDirectory)
        {
            if (caseOnly)
            {
                // Directory.Move refuses a case-only rename; go through a unique temporary name.
                var temporary = Path.Combine(parent, $".nexus-rename-{Guid.NewGuid():N}");
                Directory.Move(path, temporary);
                Directory.Move(temporary, destination);
            }
            else
            {
                Directory.Move(path, destination);
            }
        }
        else
        {
            File.Move(path, destination);
        }

        return destination;
    }

    private Task<FileOperationOutcome> RunAsync(FileOperationKind kind, IReadOnlyList<string> sources, string? destinationFolder, IntPtr owner)
    {
        var items = sources.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0)
        {
            return Task.FromResult(new FileOperationOutcome(true, false, []));
        }

        if (destinationFolder is not null)
        {
            var problem = ValidateTransfer(kind, items, destinationFolder);
            if (problem is not null)
            {
                return Task.FromResult(new FileOperationOutcome(false, false, [], problem));
            }
        }

        // Pasting copies into the folder they came from behaves like Explorer: "Имя - копия".
        var renameOnCollision = kind == FileOperationKind.Copy && destinationFolder is not null
            && items.All(item => PathHelper.GetParent(item) is { } parent && PathHelper.AreEqual(parent, destinationFolder));

        return StaThread.RunAsync(() => Perform(kind, items, destinationFolder, owner, renameOnCollision), "Nexus File Operation");
    }

    internal static string? ValidateTransfer(FileOperationKind kind, IReadOnlyList<string> items, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder))
        {
            return "Папка назначения недоступна.";
        }

        foreach (var item in items)
        {
            if (Directory.Exists(item) && (PathHelper.AreEqual(item, destinationFolder) || PathHelper.IsInside(destinationFolder, item)))
            {
                return $"Нельзя {(kind == FileOperationKind.Move ? "переместить" : "скопировать")} папку «{Path.GetFileName(item)}» в саму себя.";
            }

            if (kind == FileOperationKind.Move && PathHelper.GetParent(item) is { } parent && PathHelper.AreEqual(parent, destinationFolder))
            {
                return "Элементы уже находятся в этой папке.";
            }
        }

        return null;
    }

    private FileOperationOutcome Perform(FileOperationKind kind, IReadOnlyList<string> items, string? destinationFolder, IntPtr owner, bool renameOnCollision)
    {
        IFileOperation? operation = null;
        IShellItem? destination = null;
        var shellItems = new List<IShellItem>();
        var sink = new ProgressSink();
        uint cookie = 0;
        try
        {
            operation = (IFileOperation)new FileOperationCoClass();
            Check(operation.SetOperationFlags(GetFlags(kind) | (renameOnCollision ? FofRenameOnCollision : 0)));
            if (owner != IntPtr.Zero && !Silent)
            {
                Check(operation.SetOwnerWindow(owner));
            }

            Check(operation.Advise(sink, out cookie));
            if (destinationFolder is not null)
            {
                destination = NativeMethods.CreateShellItem(destinationFolder);
            }

            foreach (var path in items)
            {
                var item = NativeMethods.TryCreateShellItem(path)
                    ?? throw new FileNotFoundException($"Элемент «{Path.GetFileName(path)}» больше не существует.", path);
                shellItems.Add(item);
                Check(kind switch
                {
                    FileOperationKind.Copy => operation.CopyItem(item, destination!, null, IntPtr.Zero),
                    FileOperationKind.Move => operation.MoveItem(item, destination!, null, IntPtr.Zero),
                    _ => operation.DeleteItem(item, IntPtr.Zero)
                });
            }

            var result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (result is NativeMethods.ErrorCancelled or NativeMethods.CopyEngineUserCancelled)
            {
                return new FileOperationOutcome(false, true, sink.Created);
            }

            if (result < 0)
            {
                return new FileOperationOutcome(false, aborted, sink.Created, Marshal.GetExceptionForHR(result)?.Message);
            }

            return new FileOperationOutcome(!aborted && sink.FailureCount == 0, aborted, sink.Created,
                sink.FailureCount > 0 ? sink.FirstFailure : null);
        }
        catch (Exception exception) when (exception is COMException or FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return new FileOperationOutcome(false, false, sink.Created, exception.Message);
        }
        finally
        {
            if (operation is not null && cookie != 0)
            {
                operation.Unadvise(cookie);
            }

            foreach (var item in shellItems)
            {
                NativeMethods.Release(item);
            }

            NativeMethods.Release(destination);
            NativeMethods.Release(operation);
        }
    }

    private uint GetFlags(FileOperationKind kind)
    {
        if (kind == FileOperationKind.DeletePermanently)
        {
            // No FOF_ALLOWUNDO and no FOF_NOCONFIRMATION: Windows shows its own
            // "permanently delete" confirmation for the whole selection.
            return FofNoConfirmMkdir | FofxShowElevationPrompt;
        }

        var flags = FofAllowUndo | FofNoConfirmMkdir | FofxAddUndoRecord | FofxShowElevationPrompt;
        if (kind == FileOperationKind.Recycle)
        {
            // We confirm in our own dialog; FOF_WANTNUKEWARNING makes Windows ask again
            // before anything that cannot go to the Recycle Bin is destroyed.
            flags |= FofNoConfirmation | FofWantNukeWarning | FofxRecycleOnDelete;
        }

        if (Silent)
        {
            flags = (flags & ~(FofxShowElevationPrompt | FofWantNukeWarning)) | FofSilent | FofNoErrorUi | FofxEarlyFailure;
            if (kind != FileOperationKind.Recycle)
            {
                flags |= FofNoConfirmation;
            }
        }

        return flags;
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [ComVisible(true)]
    private sealed class ProgressSink : IFileOperationProgressSink
    {
        private readonly List<string> _created = [];

        public IReadOnlyList<string> Created => _created;

        public int FailureCount { get; private set; }

        public string? FirstFailure { get; private set; }

        public int StartOperations() => NativeMethods.S_OK;

        public int FinishOperations(int result) => NativeMethods.S_OK;

        public int PreRenameItem(uint flags, IShellItem item, string newName) => NativeMethods.S_OK;

        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? newlyCreated) => NativeMethods.S_OK;

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => NativeMethods.S_OK;

        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? newlyCreated)
        {
            Record(result, newlyCreated);
            return NativeMethods.S_OK;
        }

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => NativeMethods.S_OK;

        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? newlyCreated)
        {
            Record(result, newlyCreated);
            return NativeMethods.S_OK;
        }

        public int PreDeleteItem(uint flags, IShellItem item) => NativeMethods.S_OK;

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated)
        {
            Record(result, null);
            return NativeMethods.S_OK;
        }

        public int PreNewItem(uint flags, IShellItem destination, string newName) => NativeMethods.S_OK;

        public int PostNewItem(uint flags, IShellItem destination, string newName, string? templateName, uint fileAttributes, int result, IShellItem? newItem) => NativeMethods.S_OK;

        public int UpdateProgress(uint workTotal, uint workSoFar) => NativeMethods.S_OK;

        public int ResetTimer() => NativeMethods.S_OK;

        public int PauseTimer() => NativeMethods.S_OK;

        public int ResumeTimer() => NativeMethods.S_OK;

        private void Record(int result, IShellItem? created)
        {
            if (result < 0 && result is not (NativeMethods.ErrorCancelled or NativeMethods.CopyEngineUserCancelled))
            {
                FailureCount++;
                FirstFailure ??= Marshal.GetExceptionForHR(result)?.Message;
            }

            if (created is not null && NativeMethods.GetDisplayName(created, Sigdn.FileSysPath) is { } path)
            {
                _created.Add(path);
            }
        }
    }
}

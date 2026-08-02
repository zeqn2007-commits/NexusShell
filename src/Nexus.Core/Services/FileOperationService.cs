using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nexus.Core.Models;

namespace Nexus.Core.Services;

public sealed class FileOperationService : IFileOperationService
{
    private const int MinimumBufferSize = 64 * 1024;
    private const int MaximumBufferSize = 8 * 1024 * 1024;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint CopyFileFailIfExists = 0x00000001;
    private const uint CopyFileDirectory = 0x00000080;

    private readonly Func<string, string, bool> _sameVolumeResolver;

    private static readonly HashSet<string> ReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public FileOperationService(
        Func<string, string, bool>? sameVolumeResolver = null)
    {
        _sameVolumeResolver = sameVolumeResolver ?? IsSameVolume;
    }

    public Task<FileOperationResult> CreateFolderAsync(
        string parentDirectory,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(parentDirectory))
            {
                return FileOperationResult.Failed("Папка назначения больше недоступна.");
            }

            var validation = ValidateName(folderName);
            if (validation is not null)
            {
                return FileOperationResult.Failed(validation);
            }

            var destinationPath = Path.Combine(parentDirectory, folderName);
            if (PathExists(destinationPath))
            {
                return FileOperationResult.Failed("Элемент с таким именем уже существует.");
            }

            Directory.CreateDirectory(destinationPath);
            return FileOperationResult.Completed("Папка создана.", destinationPath);
        }, cancellationToken);
    }

    public Task<FileOperationResult> RenameAsync(
        string sourcePath,
        string newName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = ValidateName(newName);
            if (validation is not null)
            {
                return FileOperationResult.Failed(validation);
            }

            var sourceIsDirectory = Directory.Exists(sourcePath);
            if (!sourceIsDirectory && !File.Exists(sourcePath))
            {
                return FileOperationResult.Failed("Выбранный элемент больше недоступен.");
            }

            var parent = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return FileOperationResult.Failed("Корневой диск нельзя переименовать здесь.");
            }

            var destinationPath = Path.Combine(parent, newName);
            if (string.Equals(
                    sourcePath,
                    destinationPath,
                    StringComparison.Ordinal))
            {
                return FileOperationResult.Completed("Имя не изменилось.", sourcePath);
            }

            if (string.Equals(
                    sourcePath,
                    destinationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                var temporaryPath = CreateOperationStagingPath(destinationPath);
                MovePath(sourcePath, temporaryPath);
                try
                {
                    MovePath(temporaryPath, destinationPath);
                }
                catch
                {
                    if (PathExists(temporaryPath) && !PathExists(sourcePath))
                    {
                        if (!TryMovePath(temporaryPath, sourcePath))
                        {
                            return FileOperationResult.Failed(
                                "Не удалось завершить переименование и вернуть исходное имя. "
                                + "Файл сохранён во временном расположении.",
                                temporaryPath);
                        }
                    }

                    return FileOperationResult.Failed(
                        "Не удалось изменить регистр имени. Исходный элемент сохранён.",
                        sourcePath);
                }

                return FileOperationResult.Completed(
                    "Регистр имени изменён.",
                    destinationPath);
            }

            if (PathExists(destinationPath))
            {
                return FileOperationResult.Failed("Элемент с таким именем уже существует.");
            }

            if (sourceIsDirectory)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }

            return FileOperationResult.Completed("Элемент переименован.", destinationPath);
        }, cancellationToken);
    }

    public Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        CopyAsync(
            sourcePaths,
            destinationDirectory,
            new FileOperationOptions(),
            progress: null,
            cancellationToken);

    public Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileOperationOptions options,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourcePaths,
            destinationDirectory,
            move: false,
            options,
            progress,
            cancellationToken);

    public Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        MoveAsync(
            sourcePaths,
            destinationDirectory,
            new FileOperationOptions(),
            progress: null,
            cancellationToken);

    public Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileOperationOptions options,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourcePaths,
            destinationDirectory,
            move: true,
            options,
            progress,
            cancellationToken);

    private async Task<FileOperationResult> TransferAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move,
        FileOperationOptions options,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (sourcePaths.Count == 0)
        {
            return FileOperationResult.Failed("Сначала выберите файлы или папки.");
        }

        if (!Directory.Exists(destinationDirectory))
        {
            return FileOperationResult.Failed("Папка назначения больше недоступна.");
        }

        string normalizedDestination;
        IReadOnlyList<string> sourceCandidates;
        try
        {
            normalizedDestination = NormalizePath(destinationDirectory);
            sourceCandidates = NormalizeAndRemoveNestedSources(sourcePaths);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return FileOperationResult.Failed(
                $"Путь не поддерживается: {exception.Message}");
        }
        var plans = new List<TransferPlan>(sourceCandidates.Count);
        var skippedPaths = new List<string>();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in sourceCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(sourcePath);
            if (!isDirectory && !File.Exists(sourcePath))
            {
                return FileOperationResult.Failed(
                    $"Элемент «{Path.GetFileName(sourcePath)}» больше недоступен.");
            }

            var name = Path.GetFileName(
                sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                return FileOperationResult.Failed(
                    "Корневой диск нельзя копировать или перемещать.");
            }

            var desiredDestination = Path.Combine(normalizedDestination, name);
            if (PathsEqual(sourcePath, desiredDestination))
            {
                return FileOperationResult.Failed("Источник и папка назначения совпадают.");
            }

            if (isDirectory && IsInsideDirectory(desiredDestination, sourcePath))
            {
                return FileOperationResult.Failed("Нельзя поместить папку внутрь самой себя.");
            }

            if (sourceCandidates.Any(candidate =>
                    !PathsEqual(candidate, sourcePath)
                    && PathsEqual(candidate, desiredDestination)))
            {
                return FileOperationResult.Failed(
                    $"Папка назначения «{name}» одновременно выбрана как источник.");
            }

            var destinationExists =
                PathExists(desiredDestination)
                || reservedDestinations.Contains(desiredDestination);
            var resolution = FileConflictResolution.Fail;
            if (destinationExists)
            {
                resolution = options.ConflictResolver is null
                    ? options.ConflictResolution
                    : await options.ConflictResolver(
                            new FileOperationConflict(
                                sourcePath,
                                desiredDestination,
                                isDirectory),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (resolution == FileConflictResolution.Fail)
                {
                    return FileOperationResult.Failed(
                        $"«{name}» уже существует в папке назначения.");
                }

                if (resolution == FileConflictResolution.Skip)
                {
                    skippedPaths.Add(sourcePath);
                    continue;
                }

                if (isDirectory
                    && resolution == FileConflictResolution.Replace)
                {
                    return FileOperationResult.Failed(
                        $"Папка «{name}» уже существует. "
                        + "Безопасная замена папки целиком отключена: "
                        + "выберите «Сохранить обе» или удалите старую папку отдельно.");
                }
            }

            var destinationPath = resolution == FileConflictResolution.KeepBoth
                ? CreateUniquePath(
                    desiredDestination,
                    isDirectory,
                    reservedDestinations)
                : desiredDestination;
            reservedDestinations.Add(destinationPath);

            plans.Add(new TransferPlan(
                sourcePath,
                destinationPath,
                isDirectory,
                ReplaceExisting:
                    destinationExists
                    && resolution == FileConflictResolution.Replace,
                SameVolume: _sameVolumeResolver(sourcePath, destinationPath)));
        }

        if (plans.Count == 0)
        {
            return new FileOperationResult(
                true,
                "Нет элементов для обработки.",
                [])
            {
                SkippedPaths = skippedPaths
            };
        }

        progress?.Report(new FileOperationProgress(
            FileOperationStage.Preparing,
            string.Empty,
            0,
            0,
            0,
            0,
            0));

        TransferTotals totals;
        try
        {
            totals = await Task.Run(
                    () => CalculateTotals(plans, options, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return FileOperationResult.Failed(
                $"Не удалось подготовить операцию: {exception.Message}");
        }

        var state = new TransferState(totals, progress);
        var completed = new List<TransferJournalEntry>(plans.Count);
        var warnings = new List<string>();

        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);

                var journalEntry = await ExecutePlanAsync(
                        plan,
                        move,
                        options,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                completed.Add(journalEntry);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            var recoveryPaths = Rollback(completed, warnings);
            if (recoveryPaths.Count > 0)
            {
                return FileOperationResult.Failed(
                    "Операция отменена, но автоматический откат завершён не полностью. "
                    + "Все сохранённые копии перечислены для ручной проверки.",
                    recoveryPaths.ToArray()) with
                {
                    SkippedPaths = skippedPaths,
                    Warnings = warnings
                };
            }

            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            var rollbackRecoveryPaths = Rollback(completed, warnings);
            var recoveryPaths = completed
                .Where(entry => PathExists(entry.Plan.DestinationPath))
                .Select(entry => entry.Plan.DestinationPath)
                .Concat(rollbackRecoveryPaths)
                .Concat(
                    exception is FileOperationRecoveryException recovery
                        ? recovery.RecoveryPaths
                        : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return FileOperationResult.Failed(
                $"Операция не завершена: {exception.Message}",
                recoveryPaths) with
            {
                SkippedPaths = skippedPaths,
                Warnings = warnings
            };
        }

        var cleanupRecoveryPaths = new List<string>();
        if (move)
        {
            foreach (var entry in completed.Where(entry =>
                         !entry.Plan.SameVolume))
            {
                state.Report(
                    FileOperationStage.DeletingSource,
                    entry.Plan.SourcePath);
                DeleteSnapshotSafely(
                    entry.Plan.SourcePath,
                    entry.Plan.CopiedSourceSnapshot,
                    warnings,
                    "исходный элемент");
                entry.SourceMoved = !PathExists(entry.Plan.SourcePath);
                if (!entry.SourceMoved)
                {
                    cleanupRecoveryPaths.Add(entry.Plan.SourcePath);
                    cleanupRecoveryPaths.Add(entry.Plan.DestinationPath);
                    warnings.Add(
                        "Копия создана, но исходный элемент изменился или пополнился "
                        + "во время операции и поэтому оставлен для проверки: "
                        + entry.Plan.SourcePath);
                }
            }
        }

        foreach (var entry in completed)
        {
            if (entry.BackupPath is not null)
            {
                TryDeletePath(entry.BackupPath, warnings);
                if (PathExists(entry.BackupPath))
                {
                    cleanupRecoveryPaths.Add(entry.BackupPath);
                }
            }
        }

        if (cleanupRecoveryPaths.Count > 0)
        {
            return FileOperationResult.Failed(
                "Перемещение выполнено частично: копии созданы, "
                + "а новые или изменённые исходные данные сохранены. "
                + "Проверьте перечисленные пути.",
                cleanupRecoveryPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()) with
            {
                SkippedPaths = skippedPaths,
                Warnings = warnings
            };
        }

        state.Report(
            FileOperationStage.Completed,
            string.Empty,
            forceComplete: true);

        var operation = move ? "Перемещено" : "Скопировано";
        var message = skippedPaths.Count == 0
            ? $"{operation}: {completed.Count}."
            : $"{operation}: {completed.Count}. Пропущено: {skippedPaths.Count}.";

        return new FileOperationResult(
            true,
            message,
            completed.Select(entry => entry.Plan.DestinationPath).ToArray())
        {
            SkippedPaths = skippedPaths,
            Warnings = warnings
        };
    }

    private static async Task<TransferJournalEntry> ExecutePlanAsync(
        TransferPlan plan,
        bool move,
        FileOperationOptions options,
        TransferState state,
        CancellationToken cancellationToken)
    {
        string? backupPath = null;
        var workingDestinationPath = CreateOperationStagingPath(
            plan.DestinationPath);
        var destinationCreated = false;
        var sourceMoved = false;
        IReadOnlyList<PathSnapshotEntry> destinationSnapshot = [];

        try
        {
            if (move && plan.SameVolume)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);
                state.Report(
                    FileOperationStage.Moving,
                    plan.SourcePath);
                if (PathExists(plan.DestinationPath))
                {
                    if (!plan.ReplaceExisting)
                    {
                        throw new IOException(
                            $"«{Path.GetFileName(plan.DestinationPath)}» появился в папке назначения во время операции.");
                    }

                    backupPath = CreateBackupPath(plan.DestinationPath);
                    File.Replace(
                        plan.SourcePath,
                        plan.DestinationPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    MovePath(plan.SourcePath, plan.DestinationPath);
                }

                sourceMoved = true;
                destinationCreated = true;
                destinationSnapshot = MapSnapshot(
                    plan.SourceSnapshot,
                    plan.SourcePath,
                    plan.DestinationPath);
                if (!plan.IsDirectory)
                {
                    destinationSnapshot =
                    [
                        CreateSnapshotEntry(
                            new FileInfo(plan.DestinationPath))
                    ];
                }
                state.CompletePlan(plan);

                return new TransferJournalEntry(
                    plan,
                    backupPath,
                    destinationCreated,
                    sourceMoved,
                    destinationSnapshot);
            }
            else
            {
                plan.CopiedSourceSnapshot.Clear();
                var workingPlan = plan with
                {
                    DestinationPath = workingDestinationPath,
                    ReplaceExisting = false
                };
                await CopyPlanAsync(
                        workingPlan,
                        move
                            ? FileOperationStage.Moving
                            : FileOperationStage.Copying,
                        options,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                destinationSnapshot = MapSnapshot(
                    CaptureSnapshot(
                        workingDestinationPath,
                        plan.IsDirectory,
                        cancellationToken),
                    workingDestinationPath,
                    plan.DestinationPath);
            }

            if (PathExists(plan.DestinationPath))
            {
                if (!plan.ReplaceExisting)
                {
                    throw new IOException(
                        $"«{Path.GetFileName(plan.DestinationPath)}» появился в папке назначения во время операции.");
                }

                backupPath = CreateBackupPath(plan.DestinationPath);
                File.Replace(
                    workingDestinationPath,
                    plan.DestinationPath,
                    backupPath,
                    ignoreMetadataErrors: true);
                destinationCreated = true;
            }
            else
            {
                MovePath(workingDestinationPath, plan.DestinationPath);
                destinationCreated = true;
            }
            return new TransferJournalEntry(
                plan,
                backupPath,
                destinationCreated,
                sourceMoved,
                destinationSnapshot);
        }
        catch (Exception exception)
        {
            var recoveryPaths = new List<string>();
            var sourceRecovered = !sourceMoved;

            if (sourceMoved)
            {
                if (PathExists(plan.SourcePath))
                {
                    sourceRecovered = false;
                    recoveryPaths.Add(plan.SourcePath);
                    if (PathExists(plan.DestinationPath))
                    {
                        recoveryPaths.Add(plan.DestinationPath);
                    }
                }
                else if (destinationCreated
                         && PathExists(plan.DestinationPath)
                         && SnapshotRootMatches(
                             plan.DestinationPath,
                             destinationSnapshot))
                {
                    sourceRecovered = TryMovePath(
                        plan.DestinationPath,
                        plan.SourcePath);
                    if (!sourceRecovered)
                    {
                        recoveryPaths.Add(plan.DestinationPath);
                    }
                }
                else
                {
                    sourceRecovered = false;
                    if (PathExists(plan.DestinationPath))
                    {
                        recoveryPaths.Add(plan.DestinationPath);
                    }
                }
            }

            if ((!sourceMoved || sourceRecovered)
                && PathExists(workingDestinationPath))
            {
                TryDeletePath(workingDestinationPath, warnings: null);
                if (PathExists(workingDestinationPath))
                {
                    recoveryPaths.Add(workingDestinationPath);
                }
            }

            if (destinationCreated
                && !sourceMoved
                && PathExists(plan.DestinationPath))
            {
                var cleanupWarnings = new List<string>();
                var removed = DeleteSnapshotSafely(
                    plan.DestinationPath,
                    destinationSnapshot,
                    cleanupWarnings,
                    "созданную копию");
                if (!removed)
                {
                    recoveryPaths.Add(plan.DestinationPath);
                }
            }

            if (backupPath is not null
                && PathExists(backupPath)
                && !PathExists(plan.DestinationPath))
            {
                if (!TryMovePath(backupPath, plan.DestinationPath))
                {
                    recoveryPaths.Add(backupPath);
                }
            }

            if (backupPath is not null
                && PathExists(backupPath))
            {
                recoveryPaths.Add(backupPath);
            }

            recoveryPaths = recoveryPaths
                .Where(PathExists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recoveryPaths.Count > 0)
            {
                var locations = string.Join(
                    "; ",
                    recoveryPaths);
                throw new FileOperationRecoveryException(
                    exception.Message
                    + $" Данные для восстановления сохранены: {locations}",
                    recoveryPaths,
                    exception);
            }

            throw;
        }
    }

    private static async Task CopyPlanAsync(
        TransferPlan plan,
        FileOperationStage stage,
        FileOperationOptions options,
        TransferState state,
        CancellationToken cancellationToken)
    {
        if (!plan.IsDirectory)
        {
            await CopyFileAsync(
                    plan,
                    plan.SourcePath,
                    plan.DestinationPath,
                    stage,
                    options,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // This report deliberately happens before the guard is acquired. If the
        // source changes after planning but before the guarded traversal starts,
        // the actual handle snapshot below becomes authoritative and only items
        // that are really copied are later eligible for source deletion.
        state.Report(
            stage,
            plan.SourcePath,
            forceReport: true);
        await CopyDirectoryAsync(
                plan.SourcePath,
                plan.DestinationPath,
                plan,
                stage,
                options,
                state,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectoryPath,
        string destinationDirectory,
        TransferPlan plan,
        FileOperationStage stage,
        FileOperationOptions options,
        TransferState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);

        using var sourceGuard = OperatingSystem.IsWindows()
            ? OpenSourceDirectoryGuard(sourceDirectoryPath)
            : null;
        var sourceInfo = new DirectoryInfo(sourceDirectoryPath);
        var actualSnapshot = sourceGuard is null
            ? CreateSnapshotEntry(sourceInfo)
            : CreateSnapshotEntryFromHandle(
                sourceDirectoryPath,
                isDirectory: true,
                sourceGuard);
        ThrowIfReparsePoint(actualSnapshot);
        RefreshPlanSnapshot(plan, actualSnapshot);

        CreateDestinationDirectoryWithMetadata(
            sourceDirectoryPath,
            destinationDirectory,
            actualSnapshot);

        foreach (var file in sourceInfo.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);

            await CopyFileAsync(
                    plan,
                    file.FullName,
                    Path.Combine(destinationDirectory, file.Name),
                    stage,
                    options,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);

            var destinationChild = Path.Combine(destinationDirectory, directory.Name);
            await CopyDirectoryAsync(
                    directory.FullName,
                    destinationChild,
                    plan,
                    stage,
                    options,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ApplyDirectoryBasicMetadataStrict(
            destinationDirectory,
            actualSnapshot);
        CopyDirectorySecurityStrict(
            sourceDirectoryPath,
            destinationDirectory);
        if (sourceGuard is not null)
        {
            EnsureSourceSnapshotUnchanged(
                actualSnapshot,
                CreateSnapshotEntryFromHandle(
                    sourceDirectoryPath,
                    isDirectory: true,
                    sourceGuard));
        }

        RecordCopiedSourceSnapshot(plan, actualSnapshot);
        state.CompleteItem(
            stage,
            sourceDirectoryPath);
    }

    private static async Task CopyFileAsync(
        TransferPlan plan,
        string sourcePath,
        string destinationPath,
        FileOperationStage stage,
        FileOperationOptions options,
        TransferState state,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(
                $"Связанный файл «{sourceInfo.Name}» не копируется из соображений безопасности.");
        }

        if (OperatingSystem.IsWindows())
        {
            using var sourceGuard = CreateFile(
                sourcePath,
                desiredAccess: FileReadAttributes,
                FileShare.Read,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (sourceGuard.IsInvalid)
            {
                throw new IOException(
                    $"Не удалось безопасно открыть «{sourceInfo.Name}» для копирования.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var actualSnapshot = CreateSnapshotEntryFromHandle(
                sourcePath,
                isDirectory: false,
                sourceGuard);
            ThrowIfReparsePoint(actualSnapshot);

            RefreshPlanSnapshot(plan, actualSnapshot);
            await CopyFileWithWindowsApiAsync(
                    sourcePath,
                    destinationPath,
                    stage,
                    options,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            TryCopyFileMetadata(sourcePath, destinationPath);
            EnsureSourceSnapshotUnchanged(
                actualSnapshot,
                CreateSnapshotEntryFromHandle(
                    sourcePath,
                    isDirectory: false,
                    sourceGuard));
            RecordCopiedSourceSnapshot(plan, actualSnapshot);
            state.CompleteItem(stage, sourcePath);
            return;
        }

        var bufferSize = Math.Clamp(
            options.BufferSize,
            MinimumBufferSize,
            MaximumBufferSize);

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fallbackSnapshot = CreateSnapshotEntry(sourceInfo);
        ThrowIfReparsePoint(fallbackSnapshot);
        RefreshPlanSnapshot(plan, fallbackSnapshot);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[bufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(options, cancellationToken).ConfigureAwait(false);

            var bytesRead = await source
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            state.AddBytes(
                bytesRead,
                stage,
                sourcePath);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        TryCopyFileMetadata(sourcePath, destinationPath);
        EnsureSourceSnapshotUnchanged(
            fallbackSnapshot,
            CreateSnapshotEntry(sourceInfo));
        RecordCopiedSourceSnapshot(plan, fallbackSnapshot);
        state.CompleteItem(
            stage,
            sourcePath);
    }

    private static Task CopyFileWithWindowsApiAsync(
        string sourcePath,
        string destinationPath,
        FileOperationStage stage,
        FileOperationOptions options,
        TransferState state,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long reportedBytes = 0;
                Exception? callbackFailure = null;
                var cancel = 0;
                CopyProgressRoutine callback = (
                    totalFileSize,
                    totalBytesTransferred,
                    _,
                    _,
                    _,
                    _,
                    _,
                    _,
                    _) =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancel = 1;
                            return CopyProgressResult.Cancel;
                        }

                        WaitIfPausedAsync(options, cancellationToken)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                        var delta = totalBytesTransferred - reportedBytes;
                        if (delta > 0)
                        {
                            reportedBytes = totalBytesTransferred;
                            state.AddBytes(delta, stage, sourcePath);
                        }

                        return CopyProgressResult.Continue;
                    }
                    catch (OperationCanceledException exception)
                    {
                        callbackFailure = exception;
                        cancel = 1;
                        return CopyProgressResult.Cancel;
                    }
                    catch (Exception exception)
                    {
                        callbackFailure = exception;
                        cancel = 1;
                        return CopyProgressResult.Cancel;
                    }
                };

                var copied = CopyFileEx(
                    sourcePath,
                    destinationPath,
                    callback,
                    IntPtr.Zero,
                    ref cancel,
                    CopyFileFailIfExists);
                GC.KeepAlive(callback);
                if (callbackFailure is OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (callbackFailure is not null)
                {
                    throw new IOException(
                        $"Не удалось продолжить копирование «{Path.GetFileName(sourcePath)}».",
                        callbackFailure);
                }

                if (!copied)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (cancellationToken.IsCancellationRequested
                        || cancel != 0)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new IOException(
                        $"Windows не смогла скопировать «{Path.GetFileName(sourcePath)}».",
                        new Win32Exception(error));
                }

                var defaultStreamLength = new FileInfo(sourcePath).Length;
                var remaining = defaultStreamLength - reportedBytes;
                if (remaining > 0)
                {
                    state.AddBytes(remaining, stage, sourcePath);
                }
            },
            cancellationToken);
    }

    private static TransferTotals CalculateTotals(
        IReadOnlyList<TransferPlan> plans,
        FileOperationOptions options,
        CancellationToken cancellationToken)
    {
        long bytes = 0;
        var items = 0;

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitIfPausedAsync(options, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var planBytes = 0L;
            var planItems = 0;
            plan.SourceSnapshot.Clear();

            if (!plan.IsDirectory)
            {
                var file = new FileInfo(plan.SourcePath);
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException(
                        $"Связанный файл «{file.Name}» не копируется из соображений безопасности.");
                }

                planBytes = file.Length;
                planItems = 1;
                plan.SourceSnapshot.Add(CreateSnapshotEntry(file));
                plan.TotalBytes = planBytes;
                plan.TotalItems = planItems;
                bytes = checked(bytes + planBytes);
                items += planItems;
                continue;
            }

            (planBytes, planItems) = CalculateDirectoryTotals(
                plan,
                plan.SourcePath,
                options,
                cancellationToken);

            plan.TotalBytes = planBytes;
            plan.TotalItems = planItems;
            bytes = checked(bytes + planBytes);
            items += planItems;
        }

        return new TransferTotals(bytes, items);
    }

    private static (long Bytes, int Items) CalculateDirectoryTotals(
        TransferPlan plan,
        string sourceDirectoryPath,
        FileOperationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitIfPausedAsync(options, cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        using var sourceGuard = OperatingSystem.IsWindows()
            ? OpenSourceDirectoryGuard(sourceDirectoryPath)
            : null;
        var sourceInfo = new DirectoryInfo(sourceDirectoryPath);
        var rootSnapshot = sourceGuard is null
            ? CreateSnapshotEntry(sourceInfo)
            : CreateSnapshotEntryFromHandle(
                sourceDirectoryPath,
                isDirectory: true,
                sourceGuard);
        ThrowIfReparsePoint(rootSnapshot);
        plan.SourceSnapshot.Add(rootSnapshot);

        long bytes = 0;
        var items = 1;
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitIfPausedAsync(options, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var snapshot = CreateSnapshotEntry(file);
            ThrowIfReparsePoint(snapshot);
            plan.SourceSnapshot.Add(snapshot);
            bytes = checked(bytes + snapshot.Length);
            items++;
        }

        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            var childTotals = CalculateDirectoryTotals(
                plan,
                directory.FullName,
                options,
                cancellationToken);
            bytes = checked(bytes + childTotals.Bytes);
            items = checked(items + childTotals.Items);
        }

        if (sourceGuard is not null)
        {
            EnsureSourceSnapshotUnchanged(
                rootSnapshot,
                CreateSnapshotEntryFromHandle(
                    sourceDirectoryPath,
                    isDirectory: true,
                    sourceGuard));
        }

        return (bytes, items);
    }

    private static IReadOnlyList<string> NormalizeAndRemoveNestedSources(
        IReadOnlyList<string> sourcePaths)
    {
        var normalized = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized
            .Where(candidate => !normalized.Any(other =>
                !PathsEqual(candidate, other)
                && Directory.Exists(other)
                && IsInsideDirectory(candidate, other)))
            .ToList();
    }

    private static string CreateUniquePath(
        string desiredPath,
        bool isDirectory,
        IReadOnlySet<string> reservedPaths)
    {
        var parent = Path.GetDirectoryName(desiredPath)
            ?? throw new IOException("Не удалось определить папку назначения.");
        var extension = isDirectory
            ? string.Empty
            : Path.GetExtension(desiredPath);
        var baseName = isDirectory
            ? Path.GetFileName(desiredPath)
            : Path.GetFileNameWithoutExtension(desiredPath);

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = Path.Combine(
                parent,
                $"{baseName} ({index}){extension}");
            if (!PathExists(candidate) && !reservedPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Не удалось подобрать свободное имя.");
    }

    private static string CreateBackupPath(string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new IOException("Не удалось подготовить безопасную замену.");
        string candidate;
        do
        {
            candidate = Path.Combine(
                parent,
                $".nexus-bak-{Guid.NewGuid():N}");
        }
        while (PathExists(candidate));

        return candidate;
    }

    private static string CreateOperationStagingPath(string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new IOException("Не удалось подготовить операцию.");
        string candidate;
        do
        {
            candidate = Path.Combine(
                parent,
                $".nexus-op-{Guid.NewGuid():N}");
        }
        while (PathExists(candidate));

        return candidate;
    }

    private static IReadOnlyList<string> Rollback(
        IReadOnlyList<TransferJournalEntry> completed,
        ICollection<string> warnings)
    {
        var recoveryPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (var index = completed.Count - 1; index >= 0; index--)
        {
            var entry = completed[index];
            var sourceRecovered = !entry.SourceMoved;
            try
            {
                if (entry.SourceMoved)
                {
                    if (PathExists(entry.Plan.SourcePath))
                    {
                        sourceRecovered = false;
                        recoveryPaths.Add(entry.Plan.SourcePath);
                        if (PathExists(entry.Plan.DestinationPath))
                        {
                            recoveryPaths.Add(entry.Plan.DestinationPath);
                        }

                        warnings.Add(
                            $"Откат «{Path.GetFileName(entry.Plan.SourcePath)}» "
                            + "остановлен: по исходному пути уже появился новый элемент.");
                    }
                    else if (PathExists(entry.Plan.DestinationPath)
                             && SnapshotRootMatches(
                                 entry.Plan.DestinationPath,
                                 entry.DestinationSnapshot))
                    {
                        MovePath(
                            entry.Plan.DestinationPath,
                            entry.Plan.SourcePath);
                        sourceRecovered = true;
                    }
                    else
                    {
                        sourceRecovered = false;
                        if (PathExists(entry.Plan.DestinationPath))
                        {
                            recoveryPaths.Add(entry.Plan.DestinationPath);
                        }

                        warnings.Add(
                            $"Откат «{Path.GetFileName(entry.Plan.SourcePath)}» "
                            + "не тронул изменившийся элемент назначения.");
                    }
                }

                if (!entry.SourceMoved
                    && entry.DestinationCreated
                    && PathExists(entry.Plan.DestinationPath))
                {
                    var removed = DeleteSnapshotSafely(
                        entry.Plan.DestinationPath,
                        entry.DestinationSnapshot,
                        warnings,
                        "созданную копию");
                    if (!removed)
                    {
                        recoveryPaths.Add(entry.Plan.DestinationPath);
                    }
                }

                if (entry.BackupPath is not null
                    && PathExists(entry.BackupPath)
                    && !PathExists(entry.Plan.DestinationPath))
                {
                    MovePath(entry.BackupPath, entry.Plan.DestinationPath);
                }

                if (entry.BackupPath is not null
                    && PathExists(entry.BackupPath))
                {
                    recoveryPaths.Add(entry.BackupPath);
                }

                if (entry.SourceMoved && !sourceRecovered)
                {
                    if (PathExists(entry.Plan.SourcePath))
                    {
                        recoveryPaths.Add(entry.Plan.SourcePath);
                    }

                    if (PathExists(entry.Plan.DestinationPath))
                    {
                        recoveryPaths.Add(entry.Plan.DestinationPath);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                warnings.Add(
                    $"Не удалось полностью откатить «{Path.GetFileName(entry.Plan.SourcePath)}»: "
                    + exception.Message);
                AddIfExists(recoveryPaths, entry.Plan.SourcePath);
                AddIfExists(recoveryPaths, entry.Plan.DestinationPath);
                if (entry.BackupPath is not null)
                {
                    AddIfExists(recoveryPaths, entry.BackupPath);
                }
            }
        }

        return recoveryPaths.ToArray();
    }

    private static PathSnapshotEntry CreateSnapshotEntry(
        FileSystemInfo entry)
    {
        entry.Refresh();
        var isDirectory = entry is DirectoryInfo;
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(
                entry.FullName,
                desiredAccess: FileReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint
                | (isDirectory ? FileFlagBackupSemantics : 0),
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return CreateSnapshotEntryFromHandle(
                    entry.FullName,
                    isDirectory,
                    handle);
            }
        }

        return new PathSnapshotEntry(
            NormalizePath(entry.FullName),
            isDirectory,
            entry is FileInfo file ? file.Length : 0,
            entry.CreationTimeUtc.Ticks,
            entry.LastWriteTimeUtc.Ticks,
            entry.LastWriteTimeUtc.Ticks,
            (uint)entry.Attributes,
            TryGetFileIdentity(entry.FullName));
    }

    private static PathSnapshotEntry CreateSnapshotEntryFromHandle(
        string path,
        bool isDirectory,
        SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information)
            || !GetFileInformationByHandleEx(
                handle,
                FileInformationByHandleClass.FileBasicInfo,
                out var basicInformation,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw new IOException(
                $"Не удалось прочитать свойства «{Path.GetFileName(path)}».",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var actualIsDirectory =
            ((FileAttributes)information.FileAttributes)
            .HasFlag(FileAttributes.Directory);
        if (actualIsDirectory != isDirectory)
        {
            throw new IOException(
                $"Тип элемента «{Path.GetFileName(path)}» изменился во время операции.");
        }

        return new PathSnapshotEntry(
            NormalizePath(path),
            actualIsDirectory,
            ((long)information.FileSizeHigh << 32)
            | information.FileSizeLow,
            RawFileTimeToDateTimeTicks(basicInformation.CreationTime),
            RawFileTimeToDateTimeTicks(basicInformation.LastWriteTime),
            RawFileTimeToDateTimeTicks(basicInformation.ChangeTime),
            basicInformation.FileAttributes,
            CreateFileIdentity(information));
    }

    private static SafeFileHandle OpenSourceDirectoryGuard(string path)
    {
        var handle = CreateFile(
            path,
            desiredAccess: FileReadAttributes,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"Не удалось безопасно открыть папку «{Path.GetFileName(path)}» для обхода.",
            new Win32Exception(error));
    }

    private static void ThrowIfReparsePoint(PathSnapshotEntry snapshot)
    {
        if (((FileAttributes)snapshot.Attributes)
            .HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(
                $"Связанный элемент «{Path.GetFileName(snapshot.FullPath)}» "
                + "не копируется из соображений безопасности.");
        }
    }

    private static void EnsureSourceSnapshotUnchanged(
        PathSnapshotEntry before,
        PathSnapshotEntry after)
    {
        var unchanged = before.IsDirectory == after.IsDirectory
                        && before.Identity == after.Identity
                        && before.CreationTimeUtcTicks
                        == after.CreationTimeUtcTicks
                        && before.LastWriteTimeUtcTicks
                        == after.LastWriteTimeUtcTicks
                        && before.ChangeTimeUtcTicks
                        == after.ChangeTimeUtcTicks
                        && before.Attributes == after.Attributes
                        && (before.IsDirectory
                            || before.Length == after.Length);
        if (!unchanged)
        {
            throw new IOException(
                $"Источник «{Path.GetFileName(before.FullPath)}» изменился во время копирования. "
                + "Исходные данные сохранены, непроверенная копия отменена.");
        }
    }

    private static void RecordCopiedSourceSnapshot(
        TransferPlan plan,
        PathSnapshotEntry actualSnapshot)
    {
        var index = plan.CopiedSourceSnapshot.FindIndex(entry =>
            PathsEqualWithoutResolution(
                entry.FullPath,
                actualSnapshot.FullPath));
        if (index >= 0)
        {
            plan.CopiedSourceSnapshot[index] = actualSnapshot;
        }
        else
        {
            plan.CopiedSourceSnapshot.Add(actualSnapshot);
        }
    }

    private static void RefreshPlanSnapshot(
        TransferPlan plan,
        PathSnapshotEntry actualSnapshot)
    {
        var index = plan.SourceSnapshot.FindIndex(entry =>
            PathsEqualWithoutResolution(
                entry.FullPath,
                actualSnapshot.FullPath));
        if (index >= 0)
        {
            plan.SourceSnapshot[index] = actualSnapshot;
        }
        else
        {
            plan.SourceSnapshot.Add(actualSnapshot);
            plan.TotalItems++;
            if (!actualSnapshot.IsDirectory)
            {
                plan.TotalBytes = checked(
                    plan.TotalBytes + actualSnapshot.Length);
            }
        }
    }

    private static IReadOnlyList<PathSnapshotEntry> CaptureSnapshot(
        string rootPath,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        var snapshot = new List<PathSnapshotEntry>();
        if (!isDirectory)
        {
            snapshot.Add(CreateSnapshotEntry(new FileInfo(rootPath)));
            return snapshot;
        }

        var root = new DirectoryInfo(rootPath);
        snapshot.Add(CreateSnapshotEntry(root));
        foreach (var entry in root.EnumerateFileSystemInfos(
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = false,
                         AttributesToSkip = 0
                     }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException(
                    $"Связанный элемент «{entry.Name}» не включён в безопасный снимок.");
            }

            snapshot.Add(CreateSnapshotEntry(entry));
        }

        return snapshot;
    }

    private static IReadOnlyList<PathSnapshotEntry> MapSnapshot(
        IReadOnlyList<PathSnapshotEntry> snapshot,
        string sourceRoot,
        string destinationRoot)
    {
        var normalizedSource = NormalizePath(sourceRoot);
        var normalizedDestination = NormalizePath(destinationRoot);
        return snapshot
            .Select(entry =>
            {
                var relativePath = Path.GetRelativePath(
                    normalizedSource,
                    entry.FullPath);
                var mappedPath = relativePath is "." or ""
                    ? normalizedDestination
                    : Path.Combine(normalizedDestination, relativePath);
                return entry with { FullPath = NormalizePath(mappedPath) };
            })
            .ToArray();
    }

    private static bool DeleteSnapshotSafely(
        string rootPath,
        IReadOnlyList<PathSnapshotEntry> snapshot,
        ICollection<string> warnings,
        string itemDescription)
    {
        if (!PathExists(rootPath))
        {
            return true;
        }

        var root = snapshot.FirstOrDefault(entry =>
            PathsEqualWithoutResolution(entry.FullPath, rootPath));
        if (root is null
            || !SnapshotEntryMatchesPath(root, requireFileMetadata: true))
        {
            warnings.Add(
                $"Nexus не удалил {itemDescription} «{rootPath}»: "
                + "объект или его метаданные изменились после копирования.");
            return false;
        }

        // Validate every directory before deleting even one child. Directory
        // ChangeTime covers child-list, stream, attribute and security changes
        // on NTFS. This intentionally favors retaining an entire source tree
        // over losing metadata that appeared after the copy was committed.
        foreach (var directory in snapshot.Where(entry => entry.IsDirectory))
        {
            if (PathExists(directory.FullPath)
                && !SnapshotEntryMatchesPath(
                    directory,
                    requireFileMetadata: true))
            {
                warnings.Add(
                    $"Папка изменилась после копирования и полностью сохранена: "
                    + directory.FullPath);
                return false;
            }
        }

        foreach (var file in snapshot
                     .Where(entry => !entry.IsDirectory)
                     .OrderByDescending(entry => entry.FullPath.Length))
        {
            if (!PathExists(file.FullPath))
            {
                continue;
            }

            if (!TryDeleteSnapshotEntry(file))
            {
                warnings.Add(
                    $"Файл изменился после копирования и сохранён: {file.FullPath}");
            }
        }

        foreach (var directory in snapshot
                     .Where(entry => entry.IsDirectory)
                     .OrderByDescending(entry => entry.FullPath.Length))
        {
            if (!PathExists(directory.FullPath))
            {
                continue;
            }

            if (!TryDeleteSnapshotEntry(directory))
            {
                // A non-empty directory normally means that a new or changed
                // child was intentionally kept. The root-level warning below
                // explains the partial result without flooding the UI.
                continue;
            }
        }

        return !PathExists(rootPath);
    }

    private static bool SnapshotRootMatches(
        string rootPath,
        IReadOnlyList<PathSnapshotEntry> snapshot)
    {
        var root = snapshot.FirstOrDefault(entry =>
            PathsEqualWithoutResolution(entry.FullPath, rootPath));
        return root is not null
               && SnapshotEntryMatchesPath(
                   root,
                   requireFileMetadata: !root.IsDirectory);
    }

    private static bool SnapshotEntryMatchesPath(
        PathSnapshotEntry snapshot,
        bool requireFileMetadata)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var isDirectory = Directory.Exists(snapshot.FullPath);
                if (isDirectory != snapshot.IsDirectory
                    || (!isDirectory && !File.Exists(snapshot.FullPath)))
                {
                    return false;
                }

                if (snapshot.IsDirectory)
                {
                    return Directory.GetCreationTimeUtc(snapshot.FullPath).Ticks
                           == snapshot.CreationTimeUtcTicks;
                }

                var file = new FileInfo(snapshot.FullPath);
                return !requireFileMetadata
                       || file.Length == snapshot.Length
                       && file.CreationTimeUtc.Ticks
                       == snapshot.CreationTimeUtcTicks
                       && file.LastWriteTimeUtc.Ticks
                       == snapshot.LastWriteTimeUtcTicks
                       && (uint)file.Attributes == snapshot.Attributes;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                return false;
            }
        }

        using var handle = CreateFile(
            snapshot.FullPath,
            desiredAccess: FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint
            | (snapshot.IsDirectory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        return !handle.IsInvalid
               && GetFileInformationByHandle(handle, out var information)
               && GetFileInformationByHandleEx(
                   handle,
                   FileInformationByHandleClass.FileBasicInfo,
                   out var basicInformation,
                   (uint)Marshal.SizeOf<FileBasicInformation>())
               && SnapshotMatches(
                   snapshot,
                   information,
                   basicInformation,
                   requireFileMetadata);
    }

    private static bool TryDeleteSnapshotEntry(
        PathSnapshotEntry snapshot)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!SnapshotEntryMatchesPath(
                    snapshot,
                    requireFileMetadata: !snapshot.IsDirectory))
            {
                return false;
            }

            try
            {
                if (snapshot.IsDirectory)
                {
                    if (Directory.EnumerateFileSystemEntries(snapshot.FullPath).Any())
                    {
                        return false;
                    }

                    ClearDeletionBlockingAttributes(snapshot.FullPath);
                    Directory.Delete(snapshot.FullPath, recursive: false);
                }
                else
                {
                    ClearDeletionBlockingAttributes(snapshot.FullPath);
                    File.Delete(snapshot.FullPath);
                }

                return !PathExists(snapshot.FullPath);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                return false;
            }
        }

        using var handle = CreateFile(
            snapshot.FullPath,
            DeleteAccess | FileReadAttributes | FileWriteAttributes,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint
            | (snapshot.IsDirectory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out var information)
            || !GetFileInformationByHandleEx(
                handle,
                FileInformationByHandleClass.FileBasicInfo,
                out var currentBasicInformation,
                (uint)Marshal.SizeOf<FileBasicInformation>())
            || !SnapshotMatches(
                snapshot,
                information,
                currentBasicInformation,
                requireFileMetadata: !snapshot.IsDirectory))
        {
            return false;
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
        {
            var cleared = attributes
                & ~FileAttributes.ReadOnly
                & ~FileAttributes.System;
            if (cleared == 0)
            {
                cleared = FileAttributes.Normal;
            }

            var updatedBasicInformation = new FileBasicInformation
            {
                FileAttributes = (uint)cleared
            };
            if (!SetFileInformationByHandle(
                    handle,
                    FileInformationByHandleClass.FileBasicInfo,
                    ref updatedBasicInformation,
                    (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                return false;
            }
        }

        if (snapshot.IsDirectory)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(snapshot.FullPath).Any())
                {
                    return false;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                return false;
            }
        }

        var disposition = new FileDispositionInformation
        {
            DeleteFile = true
        };
        return SetFileInformationByHandle(
            handle,
            FileInformationByHandleClass.FileDispositionInfo,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    private static bool SnapshotMatches(
        PathSnapshotEntry snapshot,
        ByHandleFileInformation information,
        FileBasicInformation basicInformation,
        bool requireFileMetadata)
    {
        var attributes = (FileAttributes)information.FileAttributes;
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (isDirectory != snapshot.IsDirectory)
        {
            return false;
        }

        var identity = CreateFileIdentity(information);
        if (snapshot.Identity is { } expectedIdentity)
        {
            if (identity != expectedIdentity)
            {
                return false;
            }
        }
        else if (RawFileTimeToDateTimeTicks(basicInformation.CreationTime)
                 != snapshot.CreationTimeUtcTicks)
        {
            return false;
        }

        if (!requireFileMetadata)
        {
            return true;
        }

        var basicMetadataMatches =
            RawFileTimeToDateTimeTicks(basicInformation.CreationTime)
            == snapshot.CreationTimeUtcTicks
            && RawFileTimeToDateTimeTicks(basicInformation.LastWriteTime)
            == snapshot.LastWriteTimeUtcTicks
            && RawFileTimeToDateTimeTicks(basicInformation.ChangeTime)
            == snapshot.ChangeTimeUtcTicks
            && basicInformation.FileAttributes == snapshot.Attributes;
        if (snapshot.IsDirectory)
        {
            return basicMetadataMatches;
        }

        var length =
            ((long)information.FileSizeHigh << 32)
            | information.FileSizeLow;
        return length == snapshot.Length
               && basicMetadataMatches;
    }

    private static FileIdentity? TryGetFileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var isDirectory = Directory.Exists(path);
        using var handle = CreateFile(
            path,
            desiredAccess: FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint
            | (isDirectory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        return !handle.IsInvalid
               && GetFileInformationByHandle(handle, out var information)
            ? CreateFileIdentity(information)
            : null;
    }

    private static FileIdentity CreateFileIdentity(
        ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32)
            | information.FileIndexLow);

    private static long RawFileTimeToDateTimeTicks(long fileTime) =>
        DateTime
            .FromFileTimeUtc(fileTime)
            .Ticks;

    private static void AddIfExists(
        ISet<string> paths,
        string path)
    {
        if (PathExists(path))
        {
            paths.Add(path);
        }
    }

    private static async ValueTask WaitIfPausedAsync(
        FileOperationOptions options,
        CancellationToken cancellationToken)
    {
        if (options.PauseController is not null)
        {
            await options.PauseController
                .WaitIfPausedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void TryCopyFileMetadata(
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            File.SetCreationTimeUtc(destinationPath, source.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destinationPath, source.LastWriteTimeUtc);
            File.SetAttributes(
                destinationPath,
                source.Attributes & ~FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            // Metadata is best-effort; the copied contents remain valid.
        }
    }

    private static void CreateDestinationDirectoryWithMetadata(
        string sourcePath,
        string destinationPath,
        PathSnapshotEntry sourceSnapshot)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(destinationPath);
            ApplyDirectoryBasicMetadataStrict(
                destinationPath,
                sourceSnapshot);
            return;
        }

        var parameters = new CopyFile2ExtendedParameters
        {
            Size = (uint)Marshal.SizeOf<CopyFile2ExtendedParameters>(),
            CopyFlags = CopyFileFailIfExists
                        | CopyFileDirectory
        };
        var result = CopyFile2(
            sourcePath,
            destinationPath,
            ref parameters);
        if (result < 0)
        {
            throw new IOException(
                $"Windows не смогла безопасно скопировать метаданные папки "
                + $"«{Path.GetFileName(sourcePath)}». Исходная папка сохранена.",
                Marshal.GetExceptionForHR(result));
        }

        ApplyDirectoryBasicMetadataStrict(destinationPath, sourceSnapshot);
    }

    private static void CopyDirectorySecurityStrict(
        string sourcePath,
        string destinationPath)
    {
        const AccessControlSections sections =
            AccessControlSections.Access
            | AccessControlSections.Owner
            | AccessControlSections.Group;
        try
        {
            var sourceSecurity = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(sourcePath),
                sections);
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(destinationPath),
                sourceSecurity);
            var destinationSecurity = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(destinationPath),
                sections);
            var sourceSddl = sourceSecurity.GetSecurityDescriptorSddlForm(sections);
            var destinationSddl =
                destinationSecurity.GetSecurityDescriptorSddlForm(sections);
            if (!string.Equals(
                    sourceSddl,
                    destinationSddl,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "Windows вернула отличающиеся права доступа после копирования папки.");
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            throw new IOException(
                $"Не удалось подтвердить перенос прав папки "
                + $"«{Path.GetFileName(sourcePath)}». Исходная папка сохранена.",
                exception);
        }
    }

    private static void ApplyDirectoryBasicMetadataStrict(
        string destinationPath,
        PathSnapshotEntry sourceSnapshot)
    {
        var expectedAttributes =
            (FileAttributes)sourceSnapshot.Attributes
            & ~FileAttributes.ReparsePoint;
        try
        {
            Directory.SetCreationTimeUtc(
                destinationPath,
                new DateTime(
                    sourceSnapshot.CreationTimeUtcTicks,
                    DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(
                destinationPath,
                new DateTime(
                    sourceSnapshot.LastWriteTimeUtcTicks,
                    DateTimeKind.Utc));
            File.SetAttributes(destinationPath, expectedAttributes);

            if (Directory.GetCreationTimeUtc(destinationPath).Ticks
                    != sourceSnapshot.CreationTimeUtcTicks
                || Directory.GetLastWriteTimeUtc(destinationPath).Ticks
                    != sourceSnapshot.LastWriteTimeUtcTicks
                || File.GetAttributes(destinationPath) != expectedAttributes)
            {
                throw new IOException(
                    "Файловая система не подтвердила сохранение атрибутов и времени папки.");
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentOutOfRangeException
            or NotSupportedException)
        {
            throw new IOException(
                $"Не удалось безопасно сохранить метаданные папки "
                + $"«{Path.GetFileName(sourceSnapshot.FullPath)}». "
                + "Исходная папка сохранена.",
                exception);
        }
    }

    private static void TryDeletePath(
        string path,
        ICollection<string>? warnings)
    {
        try
        {
            if (PathExists(path))
            {
                DeletePath(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            warnings?.Add(
                $"Не удалось удалить временный элемент «{path}»: {exception.Message}");
        }
    }

    private static bool TryMovePath(string sourcePath, string destinationPath)
    {
        try
        {
            MovePath(sourcePath, destinationPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            // The caller is already handling the original failure.
            return false;
        }
    }

    private static void MovePath(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            DeleteDirectoryTree(path);
        }
        else if (File.Exists(path))
        {
            ClearDeletionBlockingAttributes(path);
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryTree(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            ClearDeletionBlockingAttributes(directoryPath);
            Directory.Delete(directoryPath, recursive: false);
            return;
        }

        foreach (var file in directory.EnumerateFiles())
        {
            ClearDeletionBlockingAttributes(file.FullName);
            file.Delete();
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            DeleteDirectoryTree(child.FullName);
        }

        ClearDeletionBlockingAttributes(directoryPath);
        Directory.Delete(directoryPath, recursive: false);
    }

    private static void ClearDeletionBlockingAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        var cleared = attributes
            & ~FileAttributes.ReadOnly
            & ~FileAttributes.System;
        if (cleared != attributes)
        {
            File.SetAttributes(path, cleared);
        }
    }

    private static bool IsSameVolume(string leftPath, string rightPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var leftVolume = TryGetVolumeIdentity(leftPath);
            var rightVolume = TryGetVolumeIdentity(rightPath);
            if (leftVolume is not null && rightVolume is not null)
            {
                return string.Equals(
                    leftVolume,
                    rightVolume,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        var leftRoot = Path.GetPathRoot(Path.GetFullPath(leftPath));
        var rightRoot = Path.GetPathRoot(Path.GetFullPath(rightPath));
        return string.Equals(
            leftRoot?.TrimEnd(Path.DirectorySeparatorChar),
            rightRoot?.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Введите имя.";
        }

        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)
            || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Имя содержит недопустимые символы или пробелы.";
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        return ReservedNames.Contains(baseName)
            ? "Это имя зарезервировано Windows."
            : null;
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static bool IsInsideDirectory(string candidatePath, string directoryPath)
    {
        var candidate = ResolvePhysicalPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var directory = ResolvePhysicalPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string leftPath, string rightPath) =>
        string.Equals(
            NormalizePath(leftPath),
            NormalizePath(rightPath),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(
                fullPath,
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static string? TryGetVolumeIdentity(string path)
    {
        try
        {
            var existingPath = GetNearestExistingPath(path);
            var volumePath = new StringBuilder(512);
            if (!GetVolumePathName(
                    existingPath,
                    volumePath,
                    volumePath.Capacity))
            {
                return null;
            }

            var volumeName = new StringBuilder(512);
            return GetVolumeNameForVolumeMountPoint(
                volumePath.ToString(),
                volumeName,
                volumeName.Capacity)
                ? volumeName.ToString()
                : volumePath.ToString();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        var normalized = NormalizePath(path);
        if (!OperatingSystem.IsWindows())
        {
            return normalized;
        }

        try
        {
            var existingPath = GetNearestExistingPath(normalized);
            var suffix = Path.GetRelativePath(existingPath, normalized);
            using var handle = CreateFile(
                existingPath,
                desiredAccess: 0,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return normalized;
            }

            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                buffer.Capacity,
                flags: 0);
            if (length == 0)
            {
                return normalized;
            }

            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    buffer.Capacity,
                    flags: 0);
                if (length == 0 || length >= buffer.Capacity)
                {
                    return normalized;
                }
            }

            var resolvedBase = NormalizeDevicePath(buffer.ToString());
            return suffix is "." or ""
                ? NormalizePath(resolvedBase)
                : NormalizePath(Path.Combine(resolvedBase, suffix));
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return normalized;
        }
    }

    private static string GetNearestExistingPath(string path)
    {
        var current = NormalizePath(path);
        while (!PathExists(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || PathsEqualWithoutResolution(parent, current))
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static bool PathsEqualWithoutResolution(
        string leftPath,
        string rightPath) =>
        string.Equals(
            NormalizePath(leftPath),
            NormalizePath(rightPath),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDevicePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(
            devicePrefix,
            StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    private sealed record TransferPlan(
        string SourcePath,
        string DestinationPath,
        bool IsDirectory,
        bool ReplaceExisting,
        bool SameVolume)
    {
        public long TotalBytes { get; set; }

        public int TotalItems { get; set; }

        public List<PathSnapshotEntry> SourceSnapshot { get; } = [];

        public List<PathSnapshotEntry> CopiedSourceSnapshot { get; } = [];
    }

    private sealed class TransferJournalEntry(
        TransferPlan plan,
        string? backupPath,
        bool destinationCreated,
        bool sourceMoved,
        IReadOnlyList<PathSnapshotEntry> destinationSnapshot)
    {
        public TransferPlan Plan { get; } = plan;

        public string? BackupPath { get; } = backupPath;

        public bool DestinationCreated { get; } = destinationCreated;

        public bool SourceMoved { get; set; } = sourceMoved;

        public IReadOnlyList<PathSnapshotEntry> DestinationSnapshot { get; } =
            destinationSnapshot;
    }

    private sealed record PathSnapshotEntry(
        string FullPath,
        bool IsDirectory,
        long Length,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        long ChangeTimeUtcTicks,
        uint Attributes,
        FileIdentity? Identity);

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private sealed record TransferTotals(long Bytes, int Items);

    private sealed class FileOperationRecoveryException(
        string message,
        IReadOnlyList<string> recoveryPaths,
        Exception innerException)
        : IOException(message, innerException)
    {
        public IReadOnlyList<string> RecoveryPaths { get; } = recoveryPaths;
    }

    private sealed class TransferState(
        TransferTotals totals,
        IProgress<FileOperationProgress>? progress)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _processedBytes;
        private int _processedItems;
        private long _lastReportTimestamp;
        private FileOperationStage? _lastStage;
        private string? _lastPath;

        public void AddBytes(
            long bytes,
            FileOperationStage stage,
            string currentPath)
        {
            _processedBytes += bytes;
            Report(stage, currentPath);
        }

        public void CompleteItem(
            FileOperationStage stage,
            string currentPath)
        {
            _processedItems++;
            Report(
                stage,
                currentPath,
                forceReport: true);
        }

        public void CompletePlan(TransferPlan plan)
        {
            _processedBytes += plan.TotalBytes;
            _processedItems += plan.TotalItems;
            Report(
                FileOperationStage.Moving,
                plan.SourcePath,
                forceReport: true);
        }

        public void Report(
            FileOperationStage stage,
            string currentPath,
            bool forceComplete = false,
            bool forceReport = false)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var stageChanged = _lastStage != stage;
            var pathChanged = !string.Equals(
                _lastPath,
                currentPath,
                StringComparison.OrdinalIgnoreCase);
            var elapsedSinceReport =
                (now - _lastReportTimestamp) / (double)Stopwatch.Frequency;
            if (!forceComplete
                && !forceReport
                && !stageChanged
                && !pathChanged
                && elapsedSinceReport < 0.05)
            {
                return;
            }

            var processedBytes = forceComplete
                ? totals.Bytes
                : Math.Min(_processedBytes, totals.Bytes);
            var processedItems = forceComplete
                ? totals.Items
                : Math.Min(_processedItems, totals.Items);
            var elapsed = _stopwatch.Elapsed.TotalSeconds;

            try
            {
                progress.Report(new FileOperationProgress(
                    stage,
                    currentPath,
                    processedBytes,
                    totals.Bytes,
                    processedItems,
                    totals.Items,
                    elapsed > 0.05
                        ? processedBytes / elapsed
                        : 0));
            }
            catch (Exception)
            {
                // A UI progress observer must not be able to corrupt a file transaction.
            }

            _lastReportTimestamp = now;
            _lastStage = stage;
            _lastPath = currentPath;
        }
    }

    private enum FileInformationByHandleClass
    {
        FileBasicInfo = 0,
        FileDispositionInfo = 4
    }

    private enum CopyProgressResult : uint
    {
        Continue = 0,
        Cancel = 1,
        Stop = 2,
        Quiet = 3
    }

    private enum CopyProgressCallbackReason : uint
    {
        ChunkFinished = 0,
        StreamSwitch = 1
    }

    private delegate CopyProgressResult CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        CopyProgressCallbackReason callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyFile2ExtendedParameters
    {
        public uint Size;
        public uint CopyFlags;
        public IntPtr Cancel;
        public IntPtr ProgressRoutine;
        public IntPtr CallbackContext;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInformationByHandleClass informationClass,
        out FileBasicInformation information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName,
        string newFileName,
        CopyProgressRoutine progressRoutine,
        IntPtr data,
        ref int cancel,
        uint copyFlags);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int CopyFile2(
        string existingFileName,
        string newFileName,
        ref CopyFile2ExtendedParameters extendedParameters);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationByHandleClass informationClass,
        ref FileBasicInformation information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationByHandleClass informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        int filePathLength,
        uint flags);
}

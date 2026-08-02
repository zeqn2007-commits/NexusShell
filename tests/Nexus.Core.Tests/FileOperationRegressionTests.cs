using System.Diagnostics;
using System.Security.AccessControl;
using Nexus.Core.Models;
using Nexus.Core.Services;

internal static class FileOperationRegressionTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "NexusCoreFileOperations",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await VerifyConflictPoliciesAsync(root);
            await VerifyReplacementRollbackAsync(root);
            await VerifyCancellationRollbackAsync(root);
            await VerifyPauseAndProgressAsync(root);
            await VerifyDestinationRaceAsync(root);
            await VerifyCrossVolumeSourceSnapshotAsync(root);
            await VerifyCrossVolumeDirectoryMetadataAsync(root);
            await VerifyDirectoryMetadataMutationAsync(root);
            await VerifyDirectoryMetadataFailureAsync(root);
            await VerifyTemporarilyMissingSourceAsync(root);
            await VerifyDirectoryReparseSwapAsync(root);
            await VerifyDirectoryGuardLifetimeAsync(root);
            await VerifyRollbackSourceCollisionAsync(root);
            await VerifyRollbackDestinationReplacementAsync(root);
            await VerifyCaseOnlyRenameAsync(root);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task VerifyConflictPoliciesAsync(string root)
    {
        var service = new FileOperationService();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(root, "conflict-source")).FullName;
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(root, "conflict-destination")).FullName;
        var source = Path.Combine(sourceDirectory, "document.txt");
        var destination = Path.Combine(destinationDirectory, "document.txt");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(destination, "destination");

        var keptBoth = await service.CopyAsync(
            [source],
            destinationDirectory,
            new FileOperationOptions
            {
                ConflictResolution = FileConflictResolution.KeepBoth
            });
        Ensure(keptBoth.Success, "KeepBoth должен завершать копирование.");
        Ensure(
            keptBoth.ResultPaths.Count == 1
            && !string.Equals(
                keptBoth.ResultPaths[0],
                destination,
                StringComparison.OrdinalIgnoreCase)
            && await File.ReadAllTextAsync(keptBoth.ResultPaths[0]) == "source",
            "KeepBoth должен создавать отдельную корректную копию.");
        Ensure(
            await File.ReadAllTextAsync(destination) == "destination",
            "KeepBoth не должен менять существующий файл.");

        var skipped = await service.CopyAsync(
            [source],
            destinationDirectory,
            new FileOperationOptions
            {
                ConflictResolution = FileConflictResolution.Skip
            });
        Ensure(
            skipped.Success
            && skipped.ResultPaths.Count == 0
            && skipped.SkippedPaths.SequenceEqual(
                [Path.GetFullPath(source)],
                StringComparer.OrdinalIgnoreCase),
            "Skip должен явно вернуть пропущенный источник.");

        var replaced = await service.CopyAsync(
            [source],
            destinationDirectory,
            new FileOperationOptions
            {
                ConflictResolution = FileConflictResolution.Replace
            });
        Ensure(replaced.Success, "Replace должен завершать копирование.");
        Ensure(
            await File.ReadAllTextAsync(destination) == "source",
            "Replace должен записать содержимое источника.");
        Ensure(
            !Directory.EnumerateFileSystemEntries(destinationDirectory)
                .Any(path => Path.GetFileName(path).Contains(
                    ".nexus-bak-",
                    StringComparison.OrdinalIgnoreCase)),
            "После успешного Replace не должны оставаться резервные файлы.");

        var sourceFolder = Directory.CreateDirectory(
            Path.Combine(sourceDirectory, "Folder")).FullName;
        var destinationFolder = Directory.CreateDirectory(
            Path.Combine(destinationDirectory, "Folder")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(sourceFolder, "source-only.txt"),
            "source");
        await File.WriteAllTextAsync(
            Path.Combine(destinationFolder, "destination-only.txt"),
            "destination");
        var directoryReplace = await service.CopyAsync(
            [sourceFolder],
            destinationDirectory,
            new FileOperationOptions
            {
                ConflictResolution = FileConflictResolution.Replace
            });
        Ensure(
            !directoryReplace.Success
            && File.Exists(Path.Combine(destinationFolder, "destination-only.txt")),
            "Опасная замена папки целиком должна отклоняться без изменения назначения.");
    }

    private static async Task VerifyReplacementRollbackAsync(string root)
    {
        var service = new FileOperationService();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(root, "rollback-source")).FullName;
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(root, "rollback-destination")).FullName;
        var source = Path.Combine(sourceDirectory, "locked.txt");
        var destination = Path.Combine(destinationDirectory, "locked.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(destination, "original");

        await using (var lockStream = new FileStream(
                         source,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            var result = await service.CopyAsync(
                [source],
                destinationDirectory,
                new FileOperationOptions
                {
                    ConflictResolution = FileConflictResolution.Replace
                });
            Ensure(!result.Success, "Ошибка чтения должна вернуть неуспех.");
        }

        Ensure(
            await File.ReadAllTextAsync(destination) == "original",
            "При ошибке Replace обязан восстановить прежний файл назначения.");
        Ensure(
            await File.ReadAllTextAsync(source) == "new",
            "При ошибке Replace исходный файл должен сохраниться.");
    }

    private static async Task VerifyCancellationRollbackAsync(string root)
    {
        var service = new FileOperationService();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(root, "cancel-source")).FullName;
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(root, "cancel-destination")).FullName;
        var first = Path.Combine(sourceDirectory, "first.bin");
        var second = Path.Combine(sourceDirectory, "second.bin");
        await File.WriteAllBytesAsync(first, new byte[2 * 1024 * 1024]);
        await File.WriteAllBytesAsync(second, new byte[2 * 1024 * 1024]);

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (value.ProcessedItems >= 1)
            {
                cancellation.Cancel();
            }
        });

        await EnsureThrowsAsync<OperationCanceledException>(() =>
            service.MoveAsync(
                [first, second],
                destinationDirectory,
                new FileOperationOptions(),
                progress,
                cancellation.Token));

        Ensure(
            File.Exists(first) && File.Exists(second),
            "Отмена пакетного перемещения должна вернуть уже перемещённые источники.");
        Ensure(
            !File.Exists(Path.Combine(destinationDirectory, "first.bin"))
            && !File.Exists(Path.Combine(destinationDirectory, "second.bin")),
            "После отката отменённого перемещения не должно оставаться копий.");

        var largeSource = Path.Combine(sourceDirectory, "large.bin");
        await File.WriteAllBytesAsync(largeSource, new byte[8 * 1024 * 1024]);
        using var copyCancellation = new CancellationTokenSource();
        var copyProgress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (value.ProcessedBytes > 0)
            {
                copyCancellation.Cancel();
            }
        });

        await EnsureThrowsAsync<OperationCanceledException>(() =>
            service.CopyAsync(
                [largeSource],
                destinationDirectory,
                new FileOperationOptions { BufferSize = 64 * 1024 },
                copyProgress,
                copyCancellation.Token));
        Ensure(
            File.Exists(largeSource),
            "Отмена копирования не должна менять источник.");
        Ensure(
            !File.Exists(Path.Combine(destinationDirectory, "large.bin")),
            "Частичная копия должна удаляться при отмене.");
    }

    private static async Task VerifyPauseAndProgressAsync(string root)
    {
        var service = new FileOperationService();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(root, "pause-source")).FullName;
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(root, "pause-destination")).FullName;
        var source = Path.Combine(sourceDirectory, "paused.bin");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);

        var pause = new FileOperationPauseController();
        pause.Pause();
        FileOperationProgress? lastProgress = null;
        var progress = new InlineProgress<FileOperationProgress>(
            value => lastProgress = value);
        var operation = service.CopyAsync(
            [source],
            destinationDirectory,
            new FileOperationOptions
            {
                PauseController = pause,
                BufferSize = 64 * 1024
            },
            progress);

        await Task.Delay(100);
        Ensure(
            !operation.IsCompleted,
            "Операция не должна выполняться, пока включена пауза.");
        Ensure(
            !File.Exists(Path.Combine(destinationDirectory, "paused.bin")),
            "На паузе до старта не должна появляться частичная копия.");

        pause.Resume();
        var result = await operation;
        Ensure(result.Success, "После снятия паузы копирование должно завершиться.");
        Ensure(
            lastProgress is
            {
                Stage: FileOperationStage.Completed,
                Percentage: >= 100
            },
            "Финальный прогресс должен быть определённым и равным 100%.");
    }

    private static async Task VerifyDestinationRaceAsync(string root)
    {
        var service = new FileOperationService();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(root, "race-source")).FullName;
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(root, "race-destination")).FullName;
        var source = Path.Combine(sourceDirectory, "race.txt");
        var destination = Path.Combine(destinationDirectory, "race.txt");
        await File.WriteAllTextAsync(source, "source");

        var foreignCreated = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!foreignCreated && value.Stage == FileOperationStage.Preparing)
            {
                File.WriteAllText(destination, "foreign");
                foreignCreated = true;
            }
        });
        var result = await service.CopyAsync(
            [source],
            destinationDirectory,
            new FileOperationOptions(),
            progress);

        Ensure(!result.Success, "Гонка назначения должна безопасно прерывать копирование.");
        Ensure(
            await File.ReadAllTextAsync(destination) == "foreign",
            "Nexus не должен удалять или перезаписывать чужой файл, появившийся во время операции.");
        Ensure(
            !Directory.EnumerateFileSystemEntries(destinationDirectory)
                .Any(path => Path.GetFileName(path).Contains(
                    ".nexus-op-",
                    StringComparison.OrdinalIgnoreCase)),
            "После безопасно прерванной операции staging-файл должен удаляться.");
    }

    private static async Task VerifyCrossVolumeSourceSnapshotAsync(string root)
    {
        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "cross-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "cross-destination")).FullName;

        var unchangedSource = Path.Combine(sourceParent, "unchanged.txt");
        await File.WriteAllTextAsync(unchangedSource, "move me");
        var unchangedResult = await service.MoveAsync(
            [unchangedSource],
            destinationParent);
        Ensure(
            unchangedResult.Success
            && !File.Exists(unchangedSource)
            && await File.ReadAllTextAsync(
                Path.Combine(destinationParent, "unchanged.txt")) == "move me",
            "Неизменённый cross-volume источник должен удаляться после commit.");

        var readOnlySource = Path.Combine(sourceParent, "readonly.txt");
        await File.WriteAllTextAsync(readOnlySource, "readonly");
        File.SetAttributes(readOnlySource, FileAttributes.ReadOnly);
        var readOnlyResult = await service.MoveAsync(
            [readOnlySource],
            destinationParent);
        Ensure(
            readOnlyResult.Success
            && !File.Exists(readOnlySource)
            && File.Exists(Path.Combine(destinationParent, "readonly.txt")),
            "Безопасная финализация должна удалять неизменённый readonly-источник.");

        var alternateStreamSource = Path.Combine(sourceParent, "streams.txt");
        await File.WriteAllTextAsync(alternateStreamSource, "default data");
        await File.WriteAllTextAsync(
            alternateStreamSource + ":nexus-metadata",
            "alternate stream data");
        var alternateStreamResult = await service.MoveAsync(
            [alternateStreamSource],
            destinationParent);
        var alternateStreamDestination =
            Path.Combine(destinationParent, "streams.txt");
        Ensure(
            alternateStreamResult.Success
            && !File.Exists(alternateStreamSource)
            && await File.ReadAllTextAsync(
                alternateStreamDestination + ":nexus-metadata")
            == "alternate stream data",
            "Windows copy обязан сохранять NTFS alternate data streams до удаления источника.");

        var changedSource = Path.Combine(sourceParent, "changed.txt");
        await File.WriteAllTextAsync(changedSource, "original");
        var originalWriteTime = File.GetLastWriteTimeUtc(changedSource);
        var contentChanged = false;
        var changedProgress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!contentChanged
                && value.Stage == FileOperationStage.DeletingSource
                && string.Equals(
                    value.CurrentPath,
                    changedSource,
                    StringComparison.OrdinalIgnoreCase))
            {
                WaitForNextChangeTimeQuantum();
                File.WriteAllText(changedSource, "modified");
                File.SetLastWriteTimeUtc(changedSource, originalWriteTime);
                contentChanged = true;
            }
        });
        var changedResult = await service.MoveAsync(
            [changedSource],
            destinationParent,
            new FileOperationOptions(),
            changedProgress);
        Ensure(
            !changedResult.Success
            && await File.ReadAllTextAsync(changedSource) == "modified",
            "Изменённый файл того же размера с возвращённым mtime должен сохраниться по ChangeTime.");
        Ensure(
            await File.ReadAllTextAsync(
                Path.Combine(destinationParent, "changed.txt")) == "original",
            "При post-copy изменении destination должен содержать зафиксированную версию.");

        var attributeSource = Path.Combine(sourceParent, "attributes.txt");
        await File.WriteAllTextAsync(attributeSource, "attributes");
        var attributeChanged = false;
        var attributeProgress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!attributeChanged
                && value.Stage == FileOperationStage.DeletingSource
                && string.Equals(
                    value.CurrentPath,
                    attributeSource,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.SetAttributes(
                    attributeSource,
                    File.GetAttributes(attributeSource) | FileAttributes.Hidden);
                attributeChanged = true;
            }
        });
        var attributeResult = await service.MoveAsync(
            [attributeSource],
            destinationParent,
            new FileOperationOptions(),
            attributeProgress);
        Ensure(
            !attributeResult.Success
            && File.Exists(attributeSource)
            && File.GetAttributes(attributeSource).HasFlag(FileAttributes.Hidden),
            "Attribute-only mutation после copy не должна удаляться как неизменённый источник.");

        var streamMutationSource =
            Path.Combine(sourceParent, "stream-mutation.txt");
        await File.WriteAllTextAsync(streamMutationSource, "default");
        await File.WriteAllTextAsync(
            streamMutationSource + ":metadata",
            "before");
        var streamMutationWriteTime =
            File.GetLastWriteTimeUtc(streamMutationSource);
        var streamMutated = false;
        var streamMutationProgress =
            new InlineProgress<FileOperationProgress>(value =>
            {
                if (!streamMutated
                    && value.Stage == FileOperationStage.DeletingSource
                    && string.Equals(
                        value.CurrentPath,
                        streamMutationSource,
                        StringComparison.OrdinalIgnoreCase))
                {
                    WaitForNextChangeTimeQuantum();
                    File.WriteAllText(
                        streamMutationSource + ":metadata",
                        "after!");
                    File.SetLastWriteTimeUtc(
                        streamMutationSource,
                        streamMutationWriteTime);
                    streamMutated = true;
                }
            });
        var streamMutationResult = await service.MoveAsync(
            [streamMutationSource],
            destinationParent,
            new FileOperationOptions(),
            streamMutationProgress);
        Ensure(
            !streamMutationResult.Success
            && File.Exists(streamMutationSource)
            && await File.ReadAllTextAsync(
                streamMutationSource + ":metadata") == "after!",
            "Изменённый после copy ADS должен сохранять исходный файл для проверки.");

        var changingDirectory = Directory.CreateDirectory(
            Path.Combine(sourceParent, "changing")).FullName;
        var original = Path.Combine(changingDirectory, "original.bin");
        await File.WriteAllBytesAsync(original, new byte[2 * 1024 * 1024]);
        var newFile = Path.Combine(changingDirectory, "arrived-later.txt");
        var changed = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!changed
                && value.Stage == FileOperationStage.DeletingSource
                && string.Equals(
                    value.CurrentPath,
                    changingDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                WaitForNextChangeTimeQuantum();
                File.WriteAllText(newFile, "must survive");
                changed = true;
            }
        });
        var changingResult = await service.MoveAsync(
            [changingDirectory],
            destinationParent,
            new FileOperationOptions(),
            progress);

        Ensure(
            !changingResult.Success,
            "Новый исходный файл должен переводить move в явный partial result.");
        Ensure(
            File.Exists(newFile)
            && await File.ReadAllTextAsync(newFile) == "must survive",
            "Файл, появившийся после копирования, нельзя удалять.");
        Ensure(
            File.Exists(original),
            "При изменении метаданных исходной папки Nexus должен сохранить всё дерево до ручной проверки.");
        Ensure(
            File.Exists(Path.Combine(
                destinationParent,
                "changing",
                "original.bin")),
            "Зафиксированная копия должна сохраниться при partial move.");
        Ensure(
            changingResult.ResultPaths.Contains(
                changingDirectory,
                StringComparer.OrdinalIgnoreCase)
            && changingResult.ResultPaths.Contains(
                Path.Combine(destinationParent, "changing"),
                StringComparer.OrdinalIgnoreCase),
            "Partial result должен перечислять и оставшийся источник, и копию.");
    }

    private static async Task VerifyCrossVolumeDirectoryMetadataAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-metadata-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-metadata-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Metadata Folder")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(source, "payload.txt"),
            "payload");
        await File.WriteAllTextAsync(
            source + ":nexus-directory-metadata",
            "directory alternate stream");
        var expectedCreationTime = new DateTime(
            2024,
            3,
            4,
            5,
            6,
            8,
            DateTimeKind.Utc);
        var expectedWriteTime = new DateTime(
            2025,
            4,
            5,
            6,
            7,
            8,
            DateTimeKind.Utc);
        Directory.SetCreationTimeUtc(source, expectedCreationTime);
        Directory.SetLastWriteTimeUtc(source, expectedWriteTime);
        File.SetAttributes(
            source,
            File.GetAttributes(source) | FileAttributes.Hidden);
        const AccessControlSections securitySections =
            AccessControlSections.Access
            | AccessControlSections.Owner
            | AccessControlSections.Group;
        var sourceSecurity = FileSystemAclExtensions
            .GetAccessControl(new DirectoryInfo(source), securitySections)
            .GetSecurityDescriptorSddlForm(securitySections);

        var result = await service.MoveAsync([source], destinationParent);
        var destination = Path.Combine(
            destinationParent,
            Path.GetFileName(source));
        var destinationSecurity = FileSystemAclExtensions
            .GetAccessControl(
                new DirectoryInfo(destination),
                securitySections)
            .GetSecurityDescriptorSddlForm(securitySections);

        Ensure(
            result.Success && !Directory.Exists(source),
            "Cross-volume папка может удаляться только после подтверждённого переноса метаданных.");
        Ensure(
            await File.ReadAllTextAsync(
                destination + ":nexus-directory-metadata")
            == "directory alternate stream",
            "Directory ADS должен сохраняться до удаления cross-volume источника.");
        Ensure(
            Directory.GetCreationTimeUtc(destination) == expectedCreationTime
            && Directory.GetLastWriteTimeUtc(destination) == expectedWriteTime
            && File.GetAttributes(destination).HasFlag(FileAttributes.Hidden),
            "Время и атрибуты cross-volume папки должны сохраняться точно.");
        Ensure(
            string.Equals(
                sourceSecurity,
                destinationSecurity,
                StringComparison.Ordinal),
            "DACL, владелец и группа cross-volume папки должны быть подтверждены до удаления источника.");
    }

    private static async Task VerifyDirectoryMetadataMutationAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-mutation-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-mutation-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Mutable Metadata")).FullName;
        var sourceFile = Path.Combine(source, "payload.txt");
        await File.WriteAllTextAsync(sourceFile, "payload");
        await File.WriteAllTextAsync(source + ":metadata", "before");
        var mutated = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!mutated
                && value.Stage == FileOperationStage.DeletingSource
                && string.Equals(
                    value.CurrentPath,
                    source,
                    StringComparison.OrdinalIgnoreCase))
            {
                WaitForNextChangeTimeQuantum();
                File.WriteAllText(source + ":metadata", "after");
                mutated = true;
            }
        });

        var result = await service.MoveAsync(
            [source],
            destinationParent,
            new FileOperationOptions(),
            progress);
        var destination = Path.Combine(destinationParent, "Mutable Metadata");

        Ensure(
            mutated
            && !result.Success
            && Directory.Exists(source)
            && File.Exists(sourceFile),
            "Изменение directory ADS после copy должно сохранить всё исходное дерево.");
        Ensure(
            await File.ReadAllTextAsync(source + ":metadata") == "after"
            && await File.ReadAllTextAsync(destination + ":metadata") == "before",
            "Partial move должен сохранить новую source-версию ADS и зафиксированную destination-версию.");
    }

    private static async Task VerifyDirectoryMetadataFailureAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-metadata-failure-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-metadata-failure-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Locked Metadata")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(source, "payload.txt"),
            "payload");
        var lockedStreamPath = source + ":locked-metadata";
        await File.WriteAllTextAsync(lockedStreamPath, "locked");

        FileOperationResult result;
        await using (var lockedStream = new FileStream(
                         lockedStreamPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            result = await service.MoveAsync([source], destinationParent);
        }

        Ensure(
            !result.Success && Directory.Exists(source),
            "Ошибка чтения directory metadata должна завершать move fail-closed и сохранять источник.");
        Ensure(
            !Directory.Exists(Path.Combine(destinationParent, "Locked Metadata")),
            "Не подтверждённый metadata-copy нельзя коммитить в destination.");
        Ensure(
            !Directory.EnumerateFileSystemEntries(destinationParent)
                .Any(path => Path.GetFileName(path).Contains(
                    ".nexus-op-",
                    StringComparison.OrdinalIgnoreCase)),
            "После metadata failure staging-папка должна удаляться.");
    }

    private static async Task VerifyTemporarilyMissingSourceAsync(string root)
    {
        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "temporarily-missing-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "temporarily-missing-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Project")).FullName;
        var copiedSource = Path.Combine(source, "copied.txt");
        var withheldSource = Path.Combine(source, "withheld.txt");
        var outsidePath = Path.Combine(sourceParent, "withheld.outside");
        await File.WriteAllTextAsync(copiedSource, "copied");
        await File.WriteAllTextAsync(withheldSource, "withheld");

        var withdrawn = false;
        var returned = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!withdrawn
                && value.Stage == FileOperationStage.Moving
                && string.Equals(
                    value.CurrentPath,
                    source,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Move(withheldSource, outsidePath);
                withdrawn = true;
                return;
            }

            if (withdrawn
                && !returned
                && value.Stage == FileOperationStage.DeletingSource
                && string.Equals(
                    value.CurrentPath,
                    source,
                    StringComparison.OrdinalIgnoreCase))
            {
                WaitForNextChangeTimeQuantum();
                File.Move(outsidePath, withheldSource);
                returned = true;
            }
        });

        var result = await service.MoveAsync(
            [source],
            destinationParent,
            new FileOperationOptions(),
            progress);
        var destination = Path.Combine(destinationParent, "Project");

        Ensure(
            withdrawn && returned,
            "Regression должен детерминированно вынести файл после planning и вернуть перед cleanup.");
        Ensure(
            !result.Success
            && File.Exists(copiedSource)
            && File.Exists(withheldSource),
            "Изменившееся после copy дерево источника должно полностью сохраниться для проверки.");
        Ensure(
            File.Exists(Path.Combine(destination, "copied.txt"))
            && !File.Exists(Path.Combine(destination, "withheld.txt")),
            "CopiedSourceSnapshot не должен включать временно отсутствовавший и фактически не скопированный файл.");
        Ensure(
            result.ResultPaths.Contains(
                source,
                StringComparer.OrdinalIgnoreCase)
            && result.ResultPaths.Contains(
                destination,
                StringComparer.OrdinalIgnoreCase),
            "Явный partial result должен перечислять сохранённый источник и созданную копию.");
    }

    private static async Task VerifyDirectoryReparseSwapAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "reparse-swap-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "reparse-swap-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Real Project")).FullName;
        var preservedSource = Path.Combine(sourceParent, "Real Project.original");
        var attacker = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Attacker Target")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(source, "must-survive.txt"),
            "source");
        await File.WriteAllTextAsync(
            Path.Combine(attacker, "must-not-copy.txt"),
            "attacker");

        var swapped = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (swapped
                || value.Stage != FileOperationStage.Moving
                || !string.Equals(
                    value.CurrentPath,
                    source,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.Move(source, preservedSource);
            CreateJunction(source, attacker);
            swapped = true;
        });

        try
        {
            var result = await service.MoveAsync(
                [source],
                destinationParent,
                new FileOperationOptions(),
                progress);
            var destination = Path.Combine(destinationParent, "Real Project");

            Ensure(swapped, "Regression должен выполнить reparse swap до открытия guarded handle.");
            Ensure(
                !result.Success
                && File.Exists(Path.Combine(preservedSource, "must-survive.txt")),
                "OPEN_REPARSE_POINT обязан отклонять подмену папки и сохранять настоящий источник.");
            Ensure(
                !File.Exists(Path.Combine(destination, "must-not-copy.txt")),
                "Nexus не должен переходить по подставленному junction при копировании.");
        }
        finally
        {
            if (Directory.Exists(source)
                && File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(source, recursive: false);
            }

            if (!Directory.Exists(source) && Directory.Exists(preservedSource))
            {
                Directory.Move(preservedSource, source);
            }
        }
    }

    private static async Task VerifyDirectoryGuardLifetimeAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new FileOperationService((_, _) => false);
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-guard-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "directory-guard-destination")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(sourceParent, "Guarded Project")).FullName;
        var renamedSource = Path.Combine(sourceParent, "Guarded Project.renamed");
        var largeFile = Path.Combine(source, "large.bin");
        await File.WriteAllBytesAsync(largeFile, new byte[16 * 1024 * 1024]);
        var renameAttempted = false;
        var renameBlocked = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (renameAttempted
                || value.Stage != FileOperationStage.Moving
                || value.ProcessedBytes <= 0
                || !string.Equals(
                    value.CurrentPath,
                    largeFile,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            renameAttempted = true;
            try
            {
                Directory.Move(source, renamedSource);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }
        });

        var result = await service.MoveAsync(
            [source],
            destinationParent,
            new FileOperationOptions { BufferSize = 64 * 1024 },
            progress);
        var destination = Path.Combine(destinationParent, "Guarded Project");

        Ensure(
            renameAttempted && renameBlocked,
            "Directory handle должен оставаться открытым и блокировать rename весь guarded traversal.");
        Ensure(
            result.Success
            && !Directory.Exists(source)
            && File.Exists(Path.Combine(destination, "large.bin")),
            "После заблокированной внешней подмены guarded move должен завершиться штатно.");
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new IOException("Не удалось запустить mklink для regression-теста.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0
            || !Directory.Exists(junctionPath)
            || !File.GetAttributes(junctionPath).HasFlag(
                FileAttributes.ReparsePoint))
        {
            throw new IOException(
                $"Не удалось создать test junction. {standardOutput} {standardError}".Trim());
        }
    }

    private static async Task VerifyRollbackSourceCollisionAsync(string root)
    {
        var service = new FileOperationService();
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "rollback-collision-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "rollback-collision-destination")).FullName;
        var first = Path.Combine(sourceParent, "first.txt");
        var second = Path.Combine(sourceParent, "locked.txt");
        await File.WriteAllTextAsync(first, "original first");
        await File.WriteAllTextAsync(second, "locked");

        var collisionCreated = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!collisionCreated
                && value.Stage == FileOperationStage.Moving
                && value.ProcessedItems >= 1
                && !File.Exists(first))
            {
                File.WriteAllText(first, "foreign collision");
                collisionCreated = true;
            }
        });

        FileOperationResult result;
        await using (var lockStream = new FileStream(
                         second,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            result = await service.MoveAsync(
                [first, second],
                destinationParent,
                new FileOperationOptions(),
                progress);
        }

        var committedFirst = Path.Combine(destinationParent, "first.txt");
        Ensure(!result.Success, "Ошибка второго move должна запустить откат.");
        Ensure(
            await File.ReadAllTextAsync(first) == "foreign collision",
            "Откат не должен удалять новый объект по старому source path.");
        Ensure(
            await File.ReadAllTextAsync(committedFirst) == "original first",
            "При source-коллизии оригинал должен остаться в destination для восстановления.");
        Ensure(
            result.ResultPaths.Contains(
                committedFirst,
                StringComparer.OrdinalIgnoreCase),
            "Неоткаченный оригинал должен быть явно перечислен.");
    }

    private static async Task VerifyRollbackDestinationReplacementAsync(string root)
    {
        var service = new FileOperationService();
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(root, "rollback-foreign-source")).FullName;
        var destinationParent = Directory.CreateDirectory(
            Path.Combine(root, "rollback-foreign-destination")).FullName;
        var first = Path.Combine(sourceParent, "first.txt");
        var secondDirectory = Directory.CreateDirectory(
            Path.Combine(sourceParent, "second")).FullName;
        var locked = Path.Combine(secondDirectory, "locked.txt");
        await File.WriteAllTextAsync(first, "operation copy");
        await File.WriteAllTextAsync(locked, "locked");
        var firstDestination = Path.Combine(destinationParent, "first.txt");

        var replaced = false;
        var progress = new InlineProgress<FileOperationProgress>(value =>
        {
            if (!replaced
                && value.Stage == FileOperationStage.Copying
                && string.Equals(
                    value.CurrentPath,
                    secondDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(firstDestination);
                File.WriteAllText(firstDestination, "foreign replacement");
                replaced = true;
            }
        });

        FileOperationResult result;
        await using (var lockStream = new FileStream(
                         locked,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            result = await service.CopyAsync(
                [first, secondDirectory],
                destinationParent,
                new FileOperationOptions(),
                progress);
        }

        Ensure(!result.Success, "Ошибка второго copy должна запустить откат.");
        Ensure(
            await File.ReadAllTextAsync(firstDestination)
            == "foreign replacement",
            "Rollback не должен удалять внешний replacement destination.");
        Ensure(
            result.ResultPaths.Contains(
                firstDestination,
                StringComparer.OrdinalIgnoreCase),
            "Чужой destination после неполного rollback должен быть указан.");
    }

    private static async Task VerifyCaseOnlyRenameAsync(string root)
    {
        var service = new FileOperationService();
        var directory = Directory.CreateDirectory(
            Path.Combine(root, "case-rename")).FullName;
        var source = Path.Combine(directory, "nexus.txt");
        await File.WriteAllTextAsync(source, "case");
        var result = await service.RenameAsync(source, "NEXUS.txt");
        var actualName = Directory
            .EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Single();

        Ensure(
            result.Success
            && string.Equals(actualName, "NEXUS.txt", StringComparison.Ordinal),
            "Переименование только регистра должно реально менять имя.");
    }

    private static async Task EnsureThrowsAsync<TException>(
        Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Ожидалось исключение {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WaitForNextChangeTimeQuantum()
    {
        // NTFS exposes ChangeTime in 100-ns units, but updates it from the
        // system clock. Two mutations performed by this in-process race hook
        // can therefore receive the same value when they land in one clock
        // quantum. Keep the production comparison strict and place the test
        // mutation in a later quantum so the intended signal is observable.
        Thread.Sleep(TimeSpan.FromMilliseconds(64));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Normal);
                }

                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

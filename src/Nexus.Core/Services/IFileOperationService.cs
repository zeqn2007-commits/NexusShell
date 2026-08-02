using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IFileOperationService
{
    Task<FileOperationResult> CreateFolderAsync(
        string parentDirectory,
        string folderName,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> RenameAsync(
        string sourcePath,
        string newName,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileOperationOptions options,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileOperationOptions options,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

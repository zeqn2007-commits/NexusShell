namespace Nexus.Core.Models;

public enum FileConflictResolution
{
    Fail,
    Skip,
    KeepBoth,
    Replace
}

public enum FileOperationStage
{
    Preparing,
    Copying,
    Moving,
    DeletingSource,
    Completed
}

public sealed record FileOperationConflict(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory);

public sealed class FileOperationOptions
{
    public FileConflictResolution ConflictResolution { get; init; } =
        FileConflictResolution.Fail;

    public Func<
        FileOperationConflict,
        CancellationToken,
        ValueTask<FileConflictResolution>>? ConflictResolver { get; init; }

    public FileOperationPauseController? PauseController { get; init; }

    public int BufferSize { get; init; } = 1024 * 1024;
}

public sealed record FileOperationProgress(
    FileOperationStage Stage,
    string CurrentPath,
    long ProcessedBytes,
    long TotalBytes,
    int ProcessedItems,
    int TotalItems,
    double BytesPerSecond)
{
    public double Percentage
    {
        get
        {
            if (Stage == FileOperationStage.Completed)
            {
                return 100d;
            }

            var byteProgress = TotalBytes > 0
                ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0d, 1d)
                : 0d;
            var itemProgress = TotalItems > 0
                ? Math.Clamp((double)ProcessedItems / TotalItems, 0d, 1d)
                : 0d;
            var combined = TotalBytes > 0 && TotalItems > 0
                ? byteProgress * 0.9d + itemProgress * 0.1d
                : TotalBytes > 0
                    ? byteProgress
                    : itemProgress;
            return Math.Min(combined * 100d, 99d);
        }
    }
}

public sealed class FileOperationPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _resumeSource;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumeSource is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _resumeSource ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? resumeSource;
        lock (_gate)
        {
            resumeSource = _resumeSource;
            _resumeSource = null;
        }

        resumeSource?.TrySetResult(true);
    }

    internal async ValueTask WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? resumeTask;
        lock (_gate)
        {
            resumeTask = _resumeSource?.Task;
        }

        if (resumeTask is not null)
        {
            await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

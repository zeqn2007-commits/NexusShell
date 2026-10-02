namespace Nexus.Core.IO;

/// <summary>
/// Watches one folder (non-recursive) and raises a single debounced
/// <see cref="Changed"/> event for a burst of file system notifications.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _disposed;

    private FolderWatcher(string path, TimeSpan debounce)
    {
        _debounce = debounce;
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                | NotifyFilters.Size | NotifyFilters.Attributes
        };
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    public event EventHandler? Changed;

    public static FolderWatcher? TryCreate(string path, TimeSpan? debounce = null)
    {
        try
        {
            return Directory.Exists(path) ? new FolderWatcher(path, debounce ?? TimeSpan.FromMilliseconds(400)) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

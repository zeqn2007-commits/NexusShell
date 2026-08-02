using System.Text.Json;

namespace Nexus.Core.Services;

public sealed class FavoritesService : IFavoritesService
{
    private readonly string _storagePath;
    private readonly HashSet<string> _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FavoritesService(string? storagePath = null)
    {
        _storagePath = Path.GetFullPath(
            storagePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexus Shell",
                "favorites.json"));
        _paths = LoadPaths(_storagePath);
    }

    public IReadOnlyList<string> GetPaths()
    {
        lock (_paths)
        {
            return _paths
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public bool Contains(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        lock (_paths)
        {
            return _paths.Contains(fullPath);
        }
    }

    public async Task<bool> AddAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return false;
        }

        bool changed;
        lock (_paths)
        {
            changed = _paths.Add(fullPath);
        }

        if (changed)
        {
            try
            {
                await SaveAsync(cancellationToken);
            }
            catch
            {
                lock (_paths)
                {
                    _paths.Remove(fullPath);
                }

                throw;
            }
        }

        return changed;
    }

    public async Task<bool> RemoveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        bool changed;
        lock (_paths)
        {
            changed = _paths.Remove(fullPath);
        }

        if (changed)
        {
            try
            {
                await SaveAsync(cancellationToken);
            }
            catch
            {
                lock (_paths)
                {
                    _paths.Add(fullPath);
                }

                throw;
            }
        }

        return changed;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "Не удалось определить папку настроек Nexus.");
            }

            Directory.CreateDirectory(directory);
            string[] snapshot;
            lock (_paths)
            {
                snapshot = _paths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var temporaryPath = _storagePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(
                    snapshot,
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static HashSet<string> LoadPaths(string storagePath)
    {
        try
        {
            if (!File.Exists(storagePath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var paths = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(storagePath));
            return new HashSet<string>(
                paths?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

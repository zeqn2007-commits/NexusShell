using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Core.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Nexus.App.Services;

internal sealed class ShellThumbnailService
{
    private const int CacheCapacity = 160;
    private const uint MinimumDecodeSize = 16;
    private const uint MaximumDecodeSize = 512;
    private static readonly HashSet<string> ContentImageExtensions = new(
        [
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = [];
    private readonly SemaphoreSlim _loadLock = new(6, 6);

    public async Task<ImageSource?> GetAsync(
        string path,
        uint size,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }

        var decodeSize = Math.Clamp(
            size,
            MinimumDecodeSize,
            MaximumDecodeSize);
        var key = CreateCacheKey(fullPath, decodeSize);
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(key, out cached))
            {
                return cached;
            }

            var source = await LoadAsync(
                fullPath,
                decodeSize,
                cancellationToken);
            if (source is not null)
            {
                StoreCached(key, source);
            }

            return source;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool TryGetCached(string key, out ImageSource? source)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(key, out source);
        }
    }

    private void StoreCached(string key, ImageSource source)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(key))
            {
                _cache[key] = source;
                return;
            }

            _cache[key] = source;
            _cacheOrder.Enqueue(key);
            while (_cache.Count > CacheCapacity
                   && _cacheOrder.TryDequeue(out var expiredKey))
            {
                _cache.Remove(expiredKey);
            }
        }
    }

    private static string CreateCacheKey(string path, uint size)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return $"{size}:file:{path}:{file.LastWriteTimeUtc.Ticks}:{file.Length}";
            }

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                return $"{size}:directory:{path}:{directory.LastWriteTimeUtc.Ticks}";
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
        }

        return $"{size}:missing:{path}";
    }

    private static async Task<ImageSource?> LoadAsync(
        string path,
        uint size,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var isContentImage = ContentImageExtensions.Contains(extension);
            if (isContentImage && File.Exists(path))
            {
                return await LoadBitmapFileAsync(
                    path,
                    size,
                    cancellationToken);
            }

            var isApplication =
                extension is ".lnk" or ".url" or ".appref-ms" or ".exe";
            if (isApplication)
            {
                var shellIconPath = ShellIconCache.TryGetIconPath(path);
                if (shellIconPath is not null)
                {
                    return await LoadBitmapFileAsync(
                        shellIconPath,
                        size,
                        cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var mode = isApplication
                ? ThumbnailMode.ListView
                : ThumbnailMode.SingleItem;
            IStorageItem item = Directory.Exists(path)
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = item switch
            {
                StorageFile file => await file.GetThumbnailAsync(
                    mode,
                    size,
                    ThumbnailOptions.UseCurrentScale
                    | ThumbnailOptions.ResizeThumbnail),
                StorageFolder folder => await folder.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    size,
                    ThumbnailOptions.UseCurrentScale
                    | ThumbnailOptions.ResizeThumbnail),
                _ => null
            };
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return await LoadCachedShellIconAsync(
                    path,
                    size,
                    cancellationToken);
            }

            var image = new BitmapImage
            {
                DecodePixelWidth = (int)size,
                DecodePixelType = DecodePixelType.Physical
            };
            cancellationToken.ThrowIfCancellationRequested();
            await image.SetSourceAsync(thumbnail);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or UnauthorizedAccessException
            or IOException
            or System.Runtime.InteropServices.COMException
            or ArgumentException
            or InvalidOperationException)
        {
            return await LoadCachedShellIconAsync(
                path,
                size,
                cancellationToken);
        }
    }

    private static async Task<ImageSource?> LoadCachedShellIconAsync(
        string path,
        uint size,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = ShellIconCache.TryGetIconPath(path);
            if (cachePath is null)
            {
                return null;
            }

            return await LoadBitmapFileAsync(
                cachePath,
                size,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or UnauthorizedAccessException
            or IOException
            or System.Runtime.InteropServices.COMException
            or ArgumentException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadBitmapFileAsync(
        string path,
        uint size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path);
        cancellationToken.ThrowIfCancellationRequested();

        var properties = await file.Properties.GetImagePropertiesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var image = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical
        };
        if (properties.Height > properties.Width)
        {
            image.DecodePixelHeight = (int)size;
        }
        else
        {
            image.DecodePixelWidth = (int)size;
        }

        using var stream = await file.OpenReadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await image.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }
}

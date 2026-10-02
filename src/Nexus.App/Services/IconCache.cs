using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Core.Shell;

namespace Nexus.App.Services;

/// <summary>
/// Turns Windows Shell icons and thumbnails into XAML images and caches them.
/// Ordinary files share one icon per extension; executables, shortcuts and
/// customised folders get their own icon, exactly like Explorer.
/// Must be used from the UI thread.
/// </summary>
public sealed class IconCache(ShellImageProvider provider)
{
    private const int Capacity = 2_000;
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".appref-ms", ".msc", ".cpl", ".scr", ".cur", ".ani"
    };

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".ico",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".pdf", ".psd", ".svg"
    };

    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsThumbnail(string path) => ThumbnailExtensions.Contains(Path.GetExtension(path));

    public Task<ImageSource?> GetIconAsync(string path, bool isFolder, FileAttributes attributes, int size)
    {
        var key = IconKey(path, isFolder, attributes, size);
        return GetAsync(key, path, size, ShellImageKind.Icon);
    }

    public Task<ImageSource?> GetThumbnailAsync(string path, DateTimeOffset modified, int size) =>
        GetAsync($"thumb|{size}|{path}|{modified.UtcTicks}", path, size, ShellImageKind.Thumbnail);

    /// <summary>The icon Start shows for an app (shell:AppsFolder\…), Store tile plating included.</summary>
    public Task<ImageSource?> GetAppIconAsync(string shellPath, int size) =>
        GetAsync($"app|{size}|{shellPath}", shellPath, size, ShellImageKind.Icon);

    private static string IconKey(string path, bool isFolder, FileAttributes attributes, int size)
    {
        if (isFolder)
        {
            // Folders with desktop.ini customisation carry ReadOnly/System; known folders and
            // drive roots have their own icons too. Plain folders share one cached icon.
            var special = (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0
                || Path.GetPathRoot(path) == path
                || Nexus.Core.IO.KnownFolders.Find(path) is not null;
            return special ? $"icon|{size}|{path}" : $"icon|{size}|<folder>";
        }

        var extension = Path.GetExtension(path);
        return PerFileIconExtensions.Contains(extension) || string.IsNullOrEmpty(extension)
            ? $"icon|{size}|{path}"
            : $"icon|{size}|{extension}";
    }

    private Task<ImageSource?> GetAsync(string key, string path, int size, ShellImageKind kind)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult<ImageSource?>(cached);
        }

        if (_pending.TryGetValue(key, out var pending))
        {
            return pending;
        }

        var task = LoadAsync(key, path, size, kind);
        _pending[key] = task;
        return task;
    }

    private async Task<ImageSource?> LoadAsync(string key, string path, int size, ShellImageKind kind)
    {
        try
        {
            var image = await provider.GetAsync(path, size, kind);
            if (image is null)
            {
                return null;
            }

            // Continuation runs on the UI thread: WriteableBitmap must be created there.
            var bitmap = new WriteableBitmap(image.Width, image.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(image.Pixels);
            }

            bitmap.Invalidate();
            Store(key, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pending.Remove(key);
        }
    }

    private void Store(string key, ImageSource image)
    {
        if (_cache.TryAdd(key, image))
        {
            _order.Enqueue(key);
            while (_cache.Count > Capacity && _order.TryDequeue(out var oldest))
            {
                _cache.Remove(oldest);
            }
        }
    }
}

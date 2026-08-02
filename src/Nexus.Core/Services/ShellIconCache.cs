using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core.Services;

public static class ShellIconCache
{
    private const string CacheFormatVersion = "v2";
    private const long MaximumFingerprintFileSize = 4 * 1024 * 1024;
    private static readonly HashSet<string> ContentThumbnailExtensions = new(
        [
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string? TryGetVisualPath(string path)
    {
        if (File.Exists(path)
            && ContentThumbnailExtensions.Contains(Path.GetExtension(path)))
        {
            return Path.GetFullPath(path);
        }

        return TryGetIconPath(path);
    }

    public static string? TryGetIconPath(string path)
    {
        var isShellPath = IsShellPath(path);
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(path)
            || (!isShellPath && !File.Exists(path) && !Directory.Exists(path)))
        {
            return null;
        }

        try
        {
            var cachePath = GetCachePath(path);
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            if (isShellPath)
            {
                return TrySaveShellItemImage(path, cachePath)
                    ? cachePath
                    : null;
            }

            var info = new ShellFileInfo();
            var result = SHGetFileInfo(
                path,
                0,
                ref info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShellGetFileInfoFlags.Icon
                | ShellGetFileInfoFlags.LargeIcon);
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var borrowedIcon = Icon.FromHandle(info.IconHandle);
                using var icon = (Icon)borrowedIcon.Clone();
                using var bitmap = icon.ToBitmap();
                return TrySaveImageAtomically(bitmap, cachePath)
                    ? cachePath
                    : null;
            }
            finally
            {
                DestroyIcon(info.IconHandle);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or ExternalException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string GetCachePath(string path)
    {
        var isShellPath = IsShellPath(path);
        var fullPath = isShellPath
            ? path.Trim()
            : Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var isDirectory = !isShellPath && Directory.Exists(fullPath);
        var isDriveRoot = isDirectory
            && string.Equals(
                Path.GetPathRoot(fullPath)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        var isPathSpecific =
            isShellPath
            || isDriveRoot
            || extension is ".exe" or ".com" or ".msi" or ".bat" or ".cmd"
                or ".ps1" or ".cpl" or ".scr" or ".lnk" or ".url"
                or ".appref-ms" or ".ico";
        var identity = isShellPath
            ? $"shell:{fullPath}"
            : isDirectory
            ? isDriveRoot ? $"drive:{fullPath}" : "folder"
            : isPathSpecific
                ? fullPath
                : $"extension:{extension}";

        var fingerprint = isShellPath || !isPathSpecific
            ? string.Empty
            : GetPathFingerprint(fullPath, extension);

        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{CacheFormatVersion}|{identity}|{fingerprint}")));
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Nexus Shell",
            "IconCache",
            $"{key}.png");
    }

    private static bool TrySaveShellItemImage(
        string path,
        string cachePath)
    {
        const int RpcChangedMode = unchecked((int)0x80010106);
        var initializeResult = CoInitializeEx(
            IntPtr.Zero,
            ComInitialization.MultiThreaded);
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
        {
            return false;
        }

        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var createResult = SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref interfaceId,
                out var imageFactory);
            if (createResult < 0 || imageFactory is null)
            {
                return false;
            }

            IntPtr bitmapHandle = IntPtr.Zero;
            try
            {
                var imageResult = imageFactory.GetImage(
                    new ShellSize(64, 64),
                    ShellItemImageFlags.BiggerSizeOk
                    | ShellItemImageFlags.IconOnly,
                    out bitmapHandle);
                if (imageResult < 0 || bitmapHandle == IntPtr.Zero)
                {
                    return false;
                }

                using var bitmap = Image.FromHbitmap(bitmapHandle);
                return TrySaveImageAtomically(bitmap, cachePath);
            }
            finally
            {
                if (bitmapHandle != IntPtr.Zero)
                {
                    DeleteObject(bitmapHandle);
                }

                if (Marshal.IsComObject(imageFactory))
                {
                    Marshal.FinalReleaseComObject(imageFactory);
                }
            }
        }
        finally
        {
            if (initializeResult >= 0)
            {
                CoUninitialize();
            }
        }
    }

    private static bool IsShellPath(string? path)
    {
        return path?.StartsWith(
            "shell:",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetPathFingerprint(
        string fullPath,
        string extension)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                var contentFingerprint = extension is ".lnk" or ".url"
                    or ".appref-ms" or ".ico"
                    ? TryGetSmallFileFingerprint(file)
                    : null;
                return $"file:{file.Length}:{file.CreationTimeUtc.Ticks}:" +
                       $"{file.LastWriteTimeUtc.Ticks}:{contentFingerprint}";
            }

            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                return $"directory:{directory.CreationTimeUtc.Ticks}:" +
                       directory.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
        }

        return "unavailable";
    }

    private static string? TryGetSmallFileFingerprint(FileInfo file)
    {
        if (file.Length > MaximumFingerprintFileSize)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TrySaveImageAtomically(Image image, string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(cachePath))
        {
            return true;
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Save(temporaryPath, ImageFormat.Png);
            try
            {
                File.Move(temporaryPath, cachePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(cachePath))
            {
                // Another worker won the cache race with the same identity.
            }

            return File.Exists(cachePath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [Flags]
    private enum ShellGetFileInfoFlags : uint
    {
        Icon = 0x000000100,
        LargeIcon = 0x000000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShellSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04
    }

    [Flags]
    private enum ComInitialization : uint
    {
        MultiThreaded = 0x0
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            ShellSize size,
            ShellItemImageFlags flags,
            out IntPtr bitmapHandle);
    }

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = false)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        ShellGetFileInfoFlags flags);

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = false)]
    private static extern int SHCreateItemFromParsingName(
        string name,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)]
        out IShellItemImageFactory? imageFactory);

    [DllImport("ole32.dll", SetLastError = false)]
    private static extern int CoInitializeEx(
        IntPtr reserved,
        ComInitialization initialization);

    [DllImport("ole32.dll", SetLastError = false)]
    private static extern void CoUninitialize();

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);
}

using System.Runtime.InteropServices;
using Nexus.Core.Interop;
using Nexus.Core.Threading;

namespace Nexus.Core.Shell;

/// <summary>Premultiplied BGRA pixels ready for a WriteableBitmap.</summary>
public sealed record ShellImage(int Width, int Height, byte[] Pixels);

public enum ShellImageKind
{
    /// <summary>The icon Explorer shows for the item (file type, folder, app icon).</summary>
    Icon,

    /// <summary>A content thumbnail when one exists (photos, videos, documents), otherwise the icon.</summary>
    Thumbnail
}

/// <summary>
/// Real Windows icons and thumbnails through IShellItemImageFactory, the same
/// source Explorer uses. Work runs on a small pool of STA threads.
/// </summary>
public sealed class ShellImageProvider : IDisposable
{
    private const int EPending = unchecked((int)0x8000000A);

    // Right after sign-in Windows rebuilds its icon cache and answers "not ready yet" (E_PENDING).
    // Asking again a little later beats leaving the placeholder icon for good.
    private static readonly int[] PendingRetryDelays = [50, 100, 200, 400, 800, 1600];

    private readonly StaTaskScheduler _scheduler = new(3, "Nexus Shell Images");

    public async Task<ShellImage?> GetAsync(string path, int size, ShellImageKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        for (var attempt = 0; ; attempt++)
        {
            var (image, pending) = await _scheduler.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Load(path, size, kind);
            }, cancellationToken).ConfigureAwait(false);

            if (!pending || attempt == PendingRetryDelays.Length)
            {
                return image;
            }

            // Wait off the STA threads so other icons keep loading meanwhile.
            await Task.Delay(PendingRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _scheduler.Dispose();

    private static (ShellImage? Image, bool Pending) Load(string path, int size, ShellImageKind kind)
    {
        var interfaceId = NativeMethods.ShellItemImageFactoryInterfaceId;
        if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var created) < 0
            || created is not IShellItemImageFactory factory)
        {
            return (null, false);
        }

        var bitmap = IntPtr.Zero;
        try
        {
            var flags = kind == ShellImageKind.Icon
                ? ImageFactoryFlags.IconOnly | ImageFactoryFlags.BiggerSizeOk
                : ImageFactoryFlags.BiggerSizeOk | ImageFactoryFlags.ResizeToFit;
            var result = factory.GetImage(new NativeSize { Width = size, Height = size }, flags, out bitmap);
            if (result < 0 || bitmap == IntPtr.Zero)
            {
                return (null, result == EPending);
            }

            return (ToImage(bitmap), false);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return (null, false);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            NativeMethods.Release(factory);
        }
    }

    private static ShellImage? ToImage(IntPtr bitmap)
    {
        if (NativeMethods.GetObjectW(bitmap, Marshal.SizeOf<BitmapStruct>(), out var info) == 0
            || info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = info.Width,
            Height = -info.Height, // top-down rows
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };
        var pixels = new byte[info.Width * info.Height * 4];
        var dc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(dc, bitmap, 0, (uint)info.Height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, dc);
        }

        // Photo thumbnails often come back without an alpha channel (all zero);
        // treat them as opaque, otherwise they would render fully transparent.
        if (info.BitsPixel < 32 || IsAlphaEmpty(pixels))
        {
            for (var index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = 0xFF;
            }
        }

        return new ShellImage(info.Width, info.Height, pixels);
    }

    private static bool IsAlphaEmpty(byte[] pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

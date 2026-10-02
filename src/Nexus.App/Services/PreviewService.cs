using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nexus.App.Services;

/// <summary>What the details pane shows for a file: its own image, its text, or nothing special.</summary>
public sealed record FilePreview(ImageSource? Image, string? Text, string? Caption = null);

/// <summary>
/// Content previews for the details pane: pictures at full quality, the beginning
/// of text and code files, the first page of PDFs. Everything else falls back to
/// the Windows thumbnail. Must be awaited on the UI thread.
/// </summary>
public sealed class PreviewService
{
    private const int MaximumTextBytes = 64 * 1024;
    private const int MaximumTextCharacters = 6_000;
    private const long MaximumImageBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp", ".heic", ".avif"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".config", ".json", ".jsonc", ".xml", ".yaml", ".yml",
        ".toml", ".csv", ".tsv", ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".xaml", ".js", ".mjs", ".cjs",
        ".ts", ".tsx", ".jsx", ".py", ".rs", ".go", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".css", ".scss", ".html",
        ".htm", ".sql", ".bat", ".cmd", ".ps1", ".psm1", ".sh", ".lua", ".rb", ".php", ".gitignore", ".gitattributes",
        ".editorconfig", ".env", ".srt", ".vtt", ".reg", ".nfo"
    };

    static PreviewService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool CanPreview(string path)
    {
        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension) || TextExtensions.Contains(extension)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || (extension.Length == 0 && Path.GetFileName(path).StartsWith('.'));
    }

    public async Task<FilePreview?> GetAsync(string path, long size, int pixelWidth, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (ImageExtensions.Contains(extension) && size <= MaximumImageBytes)
            {
                return new FilePreview(LoadImage(path, pixelWidth), null);
            }

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadPdfAsync(path, pixelWidth, cancellationToken);
            }

            if (TextExtensions.Contains(extension) || (extension.Length == 0 && Path.GetFileName(path).StartsWith('.')))
            {
                var text = await Task.Run(() => ReadText(path), cancellationToken);
                return text is null ? null : new FilePreview(null, text);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // A locked, damaged or unsupported file simply has no content preview.
        }

        return null;
    }

    private static BitmapImage LoadImage(string path, int pixelWidth) => new(new Uri(path))
    {
        DecodePixelWidth = pixelWidth,
        DecodePixelType = DecodePixelType.Physical
    };

    private static async Task<FilePreview?> LoadPdfAsync(string path, int pixelWidth, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (document.PageCount == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var page = document.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)pixelWidth });
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        var pages = document.PageCount;
        return new FilePreview(image, null, $"Страница 1 из {pages}");
    }

    internal static string? ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(stream.Length, MaximumTextBytes)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> bytes = buffer.AsSpan(0, read);
        if (bytes.IndexOf((byte)0) >= 0 && !HasUnicodeBom(bytes))
        {
            return null; // binary content that merely has a text-like extension
        }

        if (stream.Length > read)
        {
            bytes = TrimIncompleteUtf8(bytes);
        }

        var text = Decode(bytes);
        if (text.Length > MaximumTextCharacters)
        {
            text = text[..MaximumTextCharacters] + "\n…";
        }
        else if (stream.Length > read)
        {
            text += "\n…";
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Drops a multi-byte UTF-8 sequence cut in half by the read limit.</summary>
    internal static ReadOnlySpan<byte> TrimIncompleteUtf8(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var value = bytes[^back];
            if ((value & 0b1100_0000) == 0b1000_0000)
            {
                continue; // continuation byte, keep looking for the lead byte
            }

            var expected = (value & 0b1110_0000) == 0b1100_0000 ? 2
                : (value & 0b1111_0000) == 0b1110_0000 ? 3
                : (value & 0b1111_1000) == 0b1111_0000 ? 4
                : 1;
            return expected > back ? bytes[..^back] : bytes;
        }

        return bytes;
    }

    private static bool HasUnicodeBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF });

    /// <summary>UTF-8/UTF-16 by BOM, otherwise strict UTF-8 with a Windows-1251 fallback for old Russian text files.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }
}

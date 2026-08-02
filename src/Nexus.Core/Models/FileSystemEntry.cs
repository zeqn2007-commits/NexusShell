namespace Nexus.Core.Models;

public sealed record FileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    DateTimeOffset ModifiedAt,
    long? SizeBytes,
    string DisplayType,
    string IconGlyph,
    string? ThumbnailPath = null,
    TorrentFileMetadata? TorrentMetadata = null)
{
    public string DisplayModified => ModifiedAt == DateTimeOffset.MinValue
        ? string.Empty
        : ModifiedAt.LocalDateTime.ToString("dd.MM.yyyy  HH:mm");

    public string DisplaySize => IsDirectory || SizeBytes is null
        ? string.Empty
        : FileSizeFormatter.Format(SizeBytes.Value);

    public double ThumbnailOpacity => string.IsNullOrWhiteSpace(ThumbnailPath)
        ? 0
        : 1;

    public string? ThumbnailSource => ToFileUri(ThumbnailPath);

    public double FallbackOpacity => ThumbnailOpacity == 0 ? 1 : 0;

    private static string? ToFileUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or UriFormatException)
        {
            return null;
        }
    }
}

internal static class FileSizeFormatter
{
    private static readonly string[] Units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string Format(long value)
    {
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {Units[unit]}"
            : $"{size:0.#} {Units[unit]}";
    }
}

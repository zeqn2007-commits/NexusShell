namespace Nexus.Core.IO;

/// <summary>An item read from a physical folder.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified,
    DateTimeOffset Created,
    FileAttributes Attributes)
{
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);

    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    public static FileEntry? TryFromPath(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new FileEntry(
                string.IsNullOrEmpty(info.Name) ? path : info.Name,
                info.FullName,
                info is DirectoryInfo,
                info is FileInfo file ? file.Length : 0,
                info.LastWriteTimeUtc,
                info.CreationTimeUtc,
                info.Attributes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

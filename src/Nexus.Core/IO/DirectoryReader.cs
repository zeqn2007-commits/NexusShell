using System.IO.Enumeration;

namespace Nexus.Core.IO;

public sealed record DirectoryReadOptions(bool ShowHidden = false, bool ShowProtectedSystemFiles = false);

/// <summary>
/// Reads folders with the low-allocation FileSystemEnumerable API: one pass,
/// attributes, sizes and times come from the same Find call.
/// </summary>
public static class DirectoryReader
{
    public static Task<IReadOnlyList<FileEntry>> ReadAsync(
        string path,
        DirectoryReadOptions options,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path, options, cancellationToken), cancellationToken);

    public static IReadOnlyList<FileEntry> Read(string path, DirectoryReadOptions options, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Папка «{path}» не найдена.");
        }

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        var enumerable = new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry entry) => new FileEntry(
                entry.FileName.ToString(),
                entry.ToFullPath(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc,
                entry.CreationTimeUtc,
                entry.Attributes),
            enumerationOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => IsVisible(entry.Attributes, entry.FileName, options)
        };

        var result = new List<FileEntry>();
        foreach (var entry in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(entry);
        }

        return result;
    }

    public static bool IsVisible(FileAttributes attributes, ReadOnlySpan<char> name, DirectoryReadOptions options)
    {
        var hidden = (attributes & FileAttributes.Hidden) != 0;
        var system = (attributes & FileAttributes.System) != 0;

        // Explorer always hides "protected operating system files" (hidden + system),
        // and plain hidden items unless the user asked to see them.
        if (hidden && system && !options.ShowProtectedSystemFiles)
        {
            return false;
        }

        if (hidden && !options.ShowHidden)
        {
            return false;
        }

        return !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }
}

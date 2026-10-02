using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace Nexus.Core.IO;

/// <summary>
/// Recursive name search inside a folder. Streams results as they are found,
/// never follows junctions or symbolic links and stops at <c>limit</c> results.
/// </summary>
public static class FileSearch
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "obj", "bin"
    };

    public static async IAsyncEnumerable<FileEntry> SearchAsync(
        string root,
        string query,
        DirectoryReadOptions options,
        int limit = 5_000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0 || !Directory.Exists(root))
        {
            yield break;
        }

        var channel = System.Threading.Channels.Channel.CreateBounded<FileEntry>(256);
        var producer = Task.Run(() =>
        {
            try
            {
                var enumerable = new FileSystemEnumerable<FileEntry>(
                    root,
                    (ref FileSystemEntry entry) => new FileEntry(
                        entry.FileName.ToString(),
                        entry.ToFullPath(),
                        entry.IsDirectory,
                        entry.IsDirectory ? 0 : entry.Length,
                        entry.LastWriteTimeUtc,
                        entry.CreationTimeUtc,
                        entry.Attributes),
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        ReturnSpecialDirectories = false
                    })
                {
                    ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                        !SkippedFolders.Contains(entry.FileName.ToString())
                        && DirectoryReader.IsVisible(entry.Attributes, entry.FileName, options),
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        DirectoryReader.IsVisible(entry.Attributes, entry.FileName, options)
                        && Matches(entry.FileName, terms)
                };

                var count = 0;
                foreach (var entry in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!channel.Writer.TryWrite(entry))
                    {
                        channel.Writer.WriteAsync(entry, cancellationToken).AsTask().GetAwaiter().GetResult();
                    }

                    if (++count >= limit)
                    {
                        break;
                    }
                }

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception is OperationCanceledException ? null : exception);
            }
        }, cancellationToken);

        await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }

        await producer.ConfigureAwait(false);
    }

    internal static bool Matches(ReadOnlySpan<char> name, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (!name.Contains(term.AsSpan(), StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

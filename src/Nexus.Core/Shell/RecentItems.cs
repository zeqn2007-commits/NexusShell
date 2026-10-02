using Nexus.Core.IO;
using Nexus.Core.Threading;

namespace Nexus.Core.Shell;

/// <summary>
/// Recent files from the Windows history (%APPDATA%\Microsoft\Windows\Recent),
/// the same list Explorer's "Recent" section shows. Only shortcuts whose target
/// still exists are returned.
/// </summary>
public sealed class RecentItems(StaTaskScheduler scheduler)
{
    public Task<IReadOnlyList<FileEntry>> GetAsync(int limit, DirectoryReadOptions options, CancellationToken cancellationToken = default) =>
        scheduler.Run(() => Load(limit, options, cancellationToken), cancellationToken);

    private static IReadOnlyList<FileEntry> Load(int limit, DirectoryReadOptions options, CancellationToken cancellationToken)
    {
        var recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentFolder))
        {
            return [];
        }

        var shortcuts = new DirectoryInfo(recentFolder)
            .EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(limit * 3);

        var result = new List<FileEntry>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in shortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ShellLinks.TryRead(shortcut.FullName)?.TargetPath;
            if (target is null || !File.Exists(target) || !seen.Add(target))
            {
                continue;
            }

            var entry = FileEntry.TryFromPath(target);
            if (entry is null || !DirectoryReader.IsVisible(entry.Attributes, entry.Name, options))
            {
                continue;
            }

            // The shortcut time is when the file was last opened, which is what "recent" means.
            result.Add(entry with { Modified = shortcut.LastWriteTimeUtc });
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }
}

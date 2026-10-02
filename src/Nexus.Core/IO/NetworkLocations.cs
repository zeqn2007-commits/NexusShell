using Nexus.Core.Shell;

namespace Nexus.Core.IO;

/// <param name="Name">The name the user gave the location.</param>
/// <param name="Target">A UNC folder (\\server\share) or, for FTP/web locations, the shortcut itself.</param>
public sealed record NetworkLocation(string Name, string Target, bool IsFolder);

/// <summary>
/// Network locations added in Explorer ("Добавить сетевое расположение"), shown on "Этот компьютер"
/// next to mapped network drives.
/// </summary>
public static class NetworkLocations
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Network Shortcuts");

    public static IReadOnlyList<NetworkLocation> Get()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var result = new List<NetworkLocation>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Folder))
            {
                // Explorer stores a location as a folder holding target.lnk; a plain .lnk works too.
                var isLocationFolder = Directory.Exists(entry);
                var shortcut = isLocationFolder ? Path.Combine(entry, "target.lnk") : entry;
                if (!shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(shortcut))
                {
                    continue;
                }

                var name = isLocationFolder ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry);
                var target = ShellLinks.TryRead(shortcut)?.TargetPath;
                result.Add(string.IsNullOrEmpty(target)
                    ? new NetworkLocation(name, shortcut, IsFolder: false)
                    : new NetworkLocation(name, target, IsFolder: true));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder is optional; an unreadable one just means no extra locations.
        }

        return result.OrderBy(location => location.Name, NaturalStringComparer.Instance).ToArray();
    }

    public static Task<IReadOnlyList<NetworkLocation>> GetAsync() => Task.Run(Get);
}

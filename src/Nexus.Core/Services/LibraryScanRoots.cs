using System.Text.Json;

namespace Nexus.Core.Services;

internal static class LibraryScanRoots
{
    private static readonly HashSet<string> SkippedDirectoryNames = new(
        [
            "$Recycle.Bin",
            "System Volume Information",
            "Windows",
            "Program Files",
            "Program Files (x86)",
            "ProgramData",
            "Recovery",
            "PerfLogs",
            "Documents and Settings",
            "AppData",
            "node_modules",
            ".git",
            "bin",
            "obj"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string DefaultLocalGameRootsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexus Shell",
        "local-game-folders.json");

    public static IReadOnlyList<string> GetKnownTorrentMetadataRoots()
    {
        var roaming = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(roaming, "uTorrent"),
            Path.Combine(roaming, "BitTorrent"),
            Path.Combine(roaming, "qBittorrent", "BT_backup"),
            Path.Combine(local, "qBittorrent", "BT_backup"),
            Path.Combine(local, "Transmission", "Torrents"),
            Path.Combine(roaming, "Transmission", "Torrents")
        ];

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryGetFullPath)
            .Where(path => path is not null && Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetDefaultRoots(
        IEnumerable<string>? additionalRoots = null,
        string? configuredRootsPath = null,
        bool includeFixedDrives = true,
        bool includeProfileFolders = true)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        if (includeProfileFolders)
        {
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory));
        }

        if (additionalRoots is not null)
        {
            roots.AddRange(additionalRoots);
        }

        roots.AddRange(LoadConfiguredRoots(
            configuredRootsPath ?? DefaultLocalGameRootsPath));

        if (includeFixedDrives)
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                        {
                            roots.Add(drive.RootDirectory.FullName);
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryGetFullPath)
            .Where(path => path is not null && Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> LoadConfiguredRoots(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<string[]>(
                       File.ReadAllText(path))
                    ?.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(TryGetFullPath)
                    .Where(item => item is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                ?? [];
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return [];
        }
    }

    public static bool ShouldTraverse(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return !SkippedDirectoryNames.Contains(info.Name)
                && !info.Attributes.HasFlag(FileAttributes.Hidden)
                && !info.Attributes.HasFlag(FileAttributes.System)
                && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }
}

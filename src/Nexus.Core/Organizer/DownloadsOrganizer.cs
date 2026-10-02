using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nexus.Core.Ai;

namespace Nexus.Core.Organizer;

public enum DownloadCategory
{
    Documents,
    Images,
    Video,
    Music,
    Archives,
    Programs,
    Torrents,
    Ai,
    Other
}

/// <summary>A file lying directly in Downloads.</summary>
public sealed record DownloadItem(string Path, DownloadCategory Category, long Size, DateTimeOffset Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public enum CleanupReason
{
    /// <summary>An installer of a program that is already installed.</summary>
    InstalledProgram,

    /// <summary>An archive with its extracted folder right next to it.</summary>
    ExtractedArchive,

    /// <summary>"name (1).ext" with the same content as "name.ext".</summary>
    Duplicate,

    /// <summary>A .torrent file a torrent client already has.</summary>
    TorrentAdded
}

/// <summary>A file that can most likely go, and what it relates to: the program, the folder or the original.</summary>
public sealed record CleanupSuggestion(DownloadItem Item, CleanupReason Reason, string Related);

/// <summary>
/// "Разбор «Загрузок»": sorts the files lying in Downloads into category folders next to them and finds the ones
/// that most likely can go. It only plans; moving and deleting is done by the UI, with undo.
/// </summary>
public static partial class DownloadsOrganizer
{
    /// <summary>Folders inside Downloads that tidied files go to; <see cref="DownloadCategory.Other"/> stays where it is.</summary>
    public static readonly IReadOnlyDictionary<DownloadCategory, string> FolderNames = new Dictionary<DownloadCategory, string>
    {
        [DownloadCategory.Documents] = "Документы",
        [DownloadCategory.Images] = "Изображения",
        [DownloadCategory.Video] = "Видео",
        [DownloadCategory.Music] = "Музыка",
        [DownloadCategory.Archives] = "Архивы",
        [DownloadCategory.Programs] = "Программы",
        [DownloadCategory.Torrents] = "Торренты",
        [DownloadCategory.Ai] = "AI"
    };

    private static readonly Dictionary<string, DownloadCategory> Extensions = BuildExtensions();

    /// <summary>Files browsers and torrent clients are still writing.</summary>
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".!ut", ".!qb", ".bc!", ".aria2"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst"
    };

    /// <summary>Words of installer names that say nothing about the program.</summary>
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "installer", "install", "installation", "update", "updater", "web", "online", "offline", "full", "portable",
        "x64", "x86", "x32", "amd64", "arm64", "win", "win32", "win64", "windows", "64bit", "32bit", "bit", "version", "release",
        "stable", "latest", "beta", "inc", "llc", "ltd", "the", "https", "http", "www", "com", "org", "net"
    };

    public static string Destination(string downloads, DownloadCategory category) => Path.Combine(downloads, FolderNames[category]);

    /// <summary>After an undone sorting: removes the category folders that were left empty (and only those).</summary>
    public static void RemoveEmptyCategoryFolders(string downloads)
    {
        foreach (var name in FolderNames.Values)
        {
            var folder = Path.Combine(downloads, name);
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be removed is merely left behind.
            }
        }
    }

    public static DownloadCategory Categorize(string path)
    {
        // AI files first: a skill packed as .zip or a prompt written as .md would otherwise pass for an archive or a document.
        if (AiFileFinder.Classify(path) is not null)
        {
            return DownloadCategory.Ai;
        }

        return Extensions.TryGetValue(Path.GetExtension(path), out var category) ? category : DownloadCategory.Other;
    }

    /// <summary>Files lying directly in Downloads; downloads in progress and files changed within the last minute are left alone.</summary>
    public static IReadOnlyList<DownloadItem> Scan(string downloads, DateTimeOffset now)
    {
        try
        {
            return new DirectoryInfo(downloads).EnumerateFiles()
                .Where(file => (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Where(file => !PartialExtensions.Contains(file.Extension))
                .Where(file => now - file.LastWriteTime > TimeSpan.FromMinutes(1))
                .Select(file => new DownloadItem(file.FullName, Categorize(file.FullName), file.Length, file.LastWriteTime))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"photos.zip" next to a non-empty "photos" folder has been extracted already.</summary>
    public static IReadOnlyList<CleanupSuggestion> FindExtractedArchives(IEnumerable<DownloadItem> items)
    {
        var suggestions = new List<CleanupSuggestion>();
        foreach (var item in items.Where(item => ArchiveExtensions.Contains(Path.GetExtension(item.Path))))
        {
            var folder = Path.GetDirectoryName(item.Path)!;
            var name = Path.GetFileNameWithoutExtension(item.Path);
            var candidates = new[] { name, Path.GetFileNameWithoutExtension(name) }.Distinct(StringComparer.OrdinalIgnoreCase);
            if (candidates.Select(candidate => Path.Combine(folder, candidate)).FirstOrDefault(HasContent) is { } extracted)
            {
                suggestions.Add(new CleanupSuggestion(item, CleanupReason.ExtractedArchive, Path.GetFileName(extracted)));
            }
        }

        return suggestions;
    }

    /// <summary>Copies named the way browsers and Explorer name them, with exactly the content of the original.</summary>
    public static IReadOnlyList<CleanupSuggestion> FindDuplicates(IEnumerable<DownloadItem> items)
    {
        var list = items.ToList();
        var byName = list.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<CleanupSuggestion>();
        foreach (var item in list)
        {
            var match = CopyName().Match(Path.GetFileNameWithoutExtension(item.Path));
            if (!match.Success)
            {
                continue;
            }

            var originalName = match.Groups["base"].Value + Path.GetExtension(item.Path);
            if (byName.TryGetValue(originalName, out var original) && original.Size == item.Size && SameContent(original.Path, item.Path))
            {
                suggestions.Add(new CleanupSuggestion(item, CleanupReason.Duplicate, original.Name));
            }
            else if (FolderNames.TryGetValue(item.Category, out var folderName)
                && Path.Combine(Path.GetDirectoryName(item.Path)!, folderName, originalName) is var sorted
                && File.Exists(sorted) && new FileInfo(sorted).Length == item.Size && SameContent(sorted, item.Path))
            {
                // The original has already been sorted into its category folder.
                suggestions.Add(new CleanupSuggestion(item, CleanupReason.Duplicate, $"{folderName} › {originalName}"));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Installers (.exe, .msi) of programs that are installed: the product name from the file's version information
    /// (or its file name) is compared with the installed programs' names.
    /// </summary>
    public static IReadOnlyList<CleanupSuggestion> FindInstalledInstallers(
        IEnumerable<DownloadItem> items,
        IEnumerable<string> installedNames,
        Func<string, string?>? productNameOf = null)
    {
        productNameOf ??= ReadProductName;
        var installed = installedNames
            .Select(name => (Name: name, Key: ProductKey(name)))
            .Where(entry => entry.Key.Length >= 4)
            .ToArray();
        var suggestions = new List<CleanupSuggestion>();
        foreach (var item in items.Where(item => item.Category == DownloadCategory.Programs))
        {
            var key = ProductKey(productNameOf(item.Path) ?? string.Empty);
            if (key.Length < 4)
            {
                key = ProductKey(Path.GetFileNameWithoutExtension(item.Path));
            }

            if (key.Length < 4)
            {
                continue;
            }

            // The same name wins; otherwise the closest program whose name starts with the other one.
            var match = installed.FirstOrDefault(entry => entry.Key == key);
            if (match.Name is null)
            {
                match = installed
                    .Where(entry => entry.Key.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(entry.Key, StringComparison.Ordinal))
                    .OrderBy(entry => Math.Abs(entry.Key.Length - key.Length))
                    .FirstOrDefault();
            }

            if (match.Name is not null)
            {
                suggestions.Add(new CleanupSuggestion(item, CleanupReason.InstalledProgram, match.Name));
            }
        }

        return suggestions;
    }

    /// <summary>"7-Zip 24.08 (x64)" → "7zip": letters and digits of the meaningful words, versions and noise left out.</summary>
    public static string ProductKey(string name)
    {
        var words = WordSplitter().Split(name.ToLowerInvariant()).Where(word => word.Length > 0).ToArray();
        var kept = words.Where((word, index) => !NoiseWords.Contains(word) && (index == 0 || (!word.All(char.IsAsciiDigit) && !VersionWord().IsMatch(word))));
        return string.Concat(kept);
    }

    private static string? ReadProductName(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new[] { info.ProductName, info.FileDescription }.FirstOrDefault(text => ProductKey(text ?? string.Empty).Length >= 4);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    private static bool HasContent(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameContent(string first, string second)
    {
        const long MaximumHashedSize = 512L * 1024 * 1024;
        try
        {
            if (new FileInfo(first).Length > MaximumHashedSize)
            {
                return false;
            }

            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, DownloadCategory> BuildExtensions()
    {
        var map = new Dictionary<string, DownloadCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(DownloadCategory category, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                map[extension] = category;
            }
        }

        Add(DownloadCategory.Documents, ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx",
            ".odp", ".epub", ".fb2", ".djvu", ".mobi", ".xps");
        Add(DownloadCategory.Images, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".svg", ".ico", ".avif",
            ".psd", ".raw", ".cr2", ".nef", ".arw", ".dng");
        Add(DownloadCategory.Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts");
        Add(DownloadCategory.Music, ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".aiff");
        Add(DownloadCategory.Archives, ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso", ".img");
        Add(DownloadCategory.Programs, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".apk", ".jar");
        Add(DownloadCategory.Torrents, ".torrent");
        return map;
    }

    [GeneratedRegex(@"^(?<base>.+?)(?: \(\d+\)| - (?:Copy|копия)(?: \(\d+\))?| — копия(?: \(\d+\))?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyName();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex WordSplitter();

    /// <summary>"v2", "24h2", "1x" and the like: version and build markers glued to letters.</summary>
    [GeneratedRegex(@"^v\d+|^\d+[a-z]{0,2}\d*$")]
    private static partial Regex VersionWord();
}

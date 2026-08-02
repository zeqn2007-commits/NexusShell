using Nexus.Core.Models;
using System.Text.Json;

namespace Nexus.Core.Services;

public sealed class FileSystemService : IFileSystemService
{
    private const string FolderGlyph = "\uE8B7";
    private const string DriveGlyph = "\uEDA2";
    private const string DocumentGlyph = "\uE8A5";
    private const string ImageGlyph = "\uEB9F";
    private const string VideoGlyph = "\uE714";
    private const string MusicGlyph = "\uE8D6";
    private const string ArchiveGlyph = "\uF012";
    private const string ApplicationGlyph = "\uECAA";
    private const string TorrentGlyph = "\uE896";
    private const string FileGlyph = "\uE7C3";
    private readonly string _preferencesPath;
    private readonly IReadOnlyList<string>? _startMenuRoots;
    private readonly IReadOnlyList<StartApplicationInfo>? _startApplications;

    public FileSystemService(
        string? preferencesPath = null,
        IEnumerable<string>? startMenuRoots = null,
        IEnumerable<StartApplicationInfo>? startApplications = null)
    {
        _preferencesPath = Path.GetFullPath(
            preferencesPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Nexus Shell",
                "file-view.json"));
        _startMenuRoots = startMenuRoots?.ToArray();
        _startApplications = startApplications?.ToArray();
        ShowHiddenItems = LoadShowHiddenItems(_preferencesPath);
    }

    public bool ShowHiddenItems { get; private set; }

    public async Task SetShowHiddenItemsAsync(
        bool value,
        CancellationToken cancellationToken = default)
    {
        if (ShowHiddenItems == value)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_preferencesPath)
            ?? throw new InvalidOperationException(
                "Не удалось определить папку настроек Nexus.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _preferencesPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(
                new FileViewPreferences(value),
                new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        File.Move(temporaryPath, _preferencesPath, overwrite: true);
        ShowHiddenItems = value;
    }

    public IReadOnlyList<NavigationTarget> GetNavigationTargets()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(profile, "Downloads");
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var projects = Path.Combine(documents, "Projects");
        var archive = Path.Combine(documents, "Архив Nexus");
        var codexRoot = Path.Combine(profile, ".codex");
        var skills = Path.Combine(codexRoot, "skills");
        var prompts = Path.Combine(codexRoot, "prompts");
        var models = Path.Combine(profile, ".ollama", "models");

        return
        [
            new("home", "Главная", "\uE80F", NavigationKind.Home),
            new("inbox", "Входящие", "\uE715", NavigationKind.Folder, downloads),
            new("recent", "Последние", "\uE823", NavigationKind.Recent),
            new("favorites", "Избранное", "\uE734", NavigationKind.Favorites),
            new("work", "Работа", "\uE821", NavigationKind.Folder, documents),
            new("projects", "Проекты", "\uE8B7", NavigationKind.OptionalFolder, projects),
            new("personal", "Личное", "\uE77B", NavigationKind.Folder, profile),
            new("media", "Медиа", "\uE8B2", NavigationKind.Media),
            new("games", "Игры", "\uE7FC", NavigationKind.Games),
            new("applications", "Приложения", "\uECAA", NavigationKind.Applications),
            new("torrents", "Торренты", "\uE896", NavigationKind.Torrents),
            new("archive", "Архив", "\uE7B8", NavigationKind.Archive, archive),
            new("ai-center", "AI-центр", "\uE945", NavigationKind.Home),
            new("models", "Модели", "\uF158", NavigationKind.OptionalFolder, models),
            new("ai-projects", "AI-проекты", "\uE8B7", NavigationKind.AiProjects),
            new("skills", "Skills", "\uE945", NavigationKind.OptionalFolder, skills),
            new("mcp", "MCP", "\uE8A9", NavigationKind.Collection, Path.Combine(codexRoot, "config.toml")),
            new("prompts", "Промты", "\uE8A5", NavigationKind.OptionalFolder, prompts),
            new("documents", "Документы", "\uE8A5", NavigationKind.Folder, documents),
            new("webapps", "Веб-приложения", "\uE943", NavigationKind.OptionalFolder, projects),
            new("resources", "Ресурсы", "\uEB9F", NavigationKind.Folder, pictures),
            new("downloads", "Загрузки", "\uE896", NavigationKind.Folder, downloads),
            new("desktop", "Рабочий стол", "\uE8FC", NavigationKind.Folder, desktop),
            new("pictures", "Изображения", "\uEB9F", NavigationKind.Folder, pictures),
            new("videos", "Видео", "\uE714", NavigationKind.Folder, videos),
            new("music", "Музыка", "\uE8D6", NavigationKind.Folder, music),
            new("computer", "Этот компьютер", "\uE7F8", NavigationKind.Computer)
            ,
            new("network", "Сеть", "\uE968", NavigationKind.Network)
        ];
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryEntriesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Папка «{path}» не найдена.");
            }

            var entries = new List<FileSystemEntry>();

            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!ShouldShowByDefault(item))
                    {
                        continue;
                    }

                    entries.Add(CreateEntry(item));
                }
                catch (UnauthorizedAccessException)
                {
                    // Один недоступный элемент не должен блокировать всю папку.
                }
                catch (IOException)
                {
                    // Элемент мог исчезнуть во время чтения каталога.
                }
            }

            return entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetDrivesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new FileSystemEntry(
                    string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"Локальный диск ({drive.Name.TrimEnd('\\')})"
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
                    drive.RootDirectory.FullName,
                    true,
                    DateTimeOffset.MinValue,
                    null,
                    $"{drive.DriveFormat} · {FileSizeFormatter.Format(drive.AvailableFreeSpace)} свободно",
                    DriveGlyph,
                    ShellIconCache.TryGetVisualPath(
                        drive.RootDirectory.FullName)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetNetworkLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DriveInfo.GetDrives()
                .Where(drive =>
                    drive.DriveType == DriveType.Network
                    && drive.IsReady)
                .Select(drive => new FileSystemEntry(
                    string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"Сетевой диск ({drive.Name.TrimEnd('\\')})"
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
                    drive.RootDirectory.FullName,
                    true,
                    DateTimeOffset.MinValue,
                    null,
                    "Сетевое расположение",
                    DriveGlyph,
                    ShellIconCache.TryGetVisualPath(
                        drive.RootDirectory.FullName)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetPathEntriesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var distinctPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            var entries = new List<FileSystemEntry>();
            foreach (var path in distinctPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    FileSystemInfo? item = Directory.Exists(path)
                        ? new DirectoryInfo(path)
                        : File.Exists(path)
                            ? new FileInfo(path)
                            : null;
                    if (item is not null)
                    {
                        entries.Add(CreateEntry(item));
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException
                    or IOException
                    or ArgumentException
                    or NotSupportedException)
                {
                }
            }

            return entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetRecentFilesAsync(
        IEnumerable<string> roots,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var distinctRoots = roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            var files = new List<FileSystemEntry>();
            var pending = new Queue<(string Path, int Depth)>();
            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var candidateLimit = Math.Min(
                5000,
                Math.Max(500, limit * 40));

            foreach (var root in distinctRoots)
            {
                pending.Enqueue((root, 0));
            }

            while (pending.Count > 0
                   && visited.Count < 3500
                   && files.Count < candidateLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, depth) = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }
                try
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 current,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var item = new FileInfo(path);
                            if (ShouldShowByDefault(item))
                            {
                                files.Add(CreateEntry(item));
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                        catch (IOException)
                        {
                        }
                    }

                    if (depth >= 6)
                    {
                        continue;
                    }

                    foreach (var directory in Directory.EnumerateDirectories(
                                 current,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (LibraryScanRoots.ShouldTraverse(directory))
                        {
                            pending.Enqueue((directory, depth + 1));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            return files
                .OrderByDescending(item => item.ModifiedAt)
                .Take(limit)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> SearchDirectoryAsync(
        string rootPath,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var fullRoot = Path.GetFullPath(rootPath);
        var normalizedQuery = query.Trim();

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Папка «{fullRoot}» не найдена.");
            }

            var results = new List<FileSystemEntry>();
            var pending = new Queue<string>();
            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(fullRoot);

            while (pending.Count > 0
                   && results.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                try
                {
                    foreach (var item in new DirectoryInfo(current)
                                 .EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!ShouldShowByDefault(item))
                        {
                            continue;
                        }

                        if (item.Name.Contains(
                                normalizedQuery,
                                StringComparison.CurrentCultureIgnoreCase))
                        {
                            results.Add(CreateEntry(item));
                            if (results.Count >= limit)
                            {
                                break;
                            }
                        }

                        if (item is DirectoryInfo directory
                            && !directory.Attributes.HasFlag(
                                FileAttributes.ReparsePoint))
                        {
                            pending.Enqueue(directory.FullName);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException
                    or IOException)
                {
                }
            }

            return results
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetTorrentFilesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetTorrentFilesAsync(
            LibraryScanRoots.GetDefaultRoots()
                .Concat(LibraryScanRoots.GetKnownTorrentMetadataRoots()),
            cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetTorrentFilesAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var scanRoots = roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
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
            })
            .Where(path => path is not null && Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            var results = new List<FileSystemEntry>();
            var pending = new Queue<(string Path, int Depth)>();
            foreach (var root in scanRoots)
            {
                pending.Enqueue((root, 0));
            }

            var visitedPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var visitedDirectories = 0;
            while (pending.Count > 0
                   && visitedDirectories < 4000
                   && results.Count < 500)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, depth) = pending.Dequeue();
                if (!visitedPaths.Add(current))
                {
                    continue;
                }

                visitedDirectories++;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 current,
                                 "*.torrent",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (results.Count >= 500)
                        {
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var item = new FileInfo(path);
                        if (ShouldShowByDefault(item))
                        {
                            results.Add(CreateEntry(item) with
                            {
                                TorrentMetadata =
                                    TorrentMetadataReader.TryRead(path)
                            });
                        }
                    }

                    if (depth >= 5)
                    {
                        continue;
                    }

                    foreach (var directory in Directory.EnumerateDirectories(
                                 current,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (LibraryScanRoots.ShouldTraverse(directory))
                        {
                            pending.Enqueue((directory, depth + 1));
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                }
            }

            return results
                .OrderByDescending(item => item.ModifiedAt)
                .ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetInstalledApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var startMenuRoots = _startMenuRoots
            ?? StartApplicationCatalog.GetDefaultStartMenuRoots();

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() =>
        {
            var applications = new List<FileSystemEntry>();
            var knownApplications = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var physicalApplications =
                StartApplicationCatalog.EnumeratePhysical(
                    startMenuRoots,
                    cancellationToken);
            var startApplications = _startApplications
                ?? StartApplicationCatalog.GetStartApps(cancellationToken);

            var mergedApplications = physicalApplications
                .Concat(startApplications)
                .Where(application =>
                    !string.IsNullOrWhiteSpace(application.Name))
                .GroupBy(
                    StartApplicationCatalog.GetStableIdentity,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(GetApplicationSourceScore)
                    .ThenBy(
                        application => application.Name,
                        StringComparer.CurrentCultureIgnoreCase)
                    .First());
            foreach (var application in mergedApplications)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = application.Name.Trim();
                if (!ShouldIncludeApplication(name)
                    || !knownApplications.Add(
                        StartApplicationCatalog.GetStableIdentity(application))
                    || string.IsNullOrWhiteSpace(application.LaunchTarget))
                {
                    continue;
                }

                var extension = Path.GetExtension(
                        application.SourcePath
                        ?? application.LaunchTarget)
                    .ToLowerInvariant();
                applications.Add(new FileSystemEntry(
                    name,
                    application.LaunchTarget,
                    false,
                    application.ModifiedAt ?? DateTimeOffset.MinValue,
                    TryGetFileLength(application.SourcePath),
                    extension.Equals(
                        ".url",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Веб-приложение"
                        : application.IsSystemComponent
                            ? "Системное приложение"
                        : application.ApplicationId is null
                            ? "Приложение"
                            : "Приложение Microsoft Store",
                    ApplicationGlyph,
                    application.IconSourcePath));
            }

            return applications
                .AsParallel()
                .WithDegreeOfParallelism(
                    Math.Clamp(Environment.ProcessorCount, 2, 8))
                .WithCancellation(cancellationToken)
                .Select(item => item with
                {
                    ThumbnailPath =
                        ShellIconCache.TryGetVisualPath(
                            item.ThumbnailPath ?? item.FullPath)
                })
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private static int GetApplicationSourceScore(
        StartApplicationInfo application)
    {
        if (application.ApplicationId is not null
            && File.Exists(application.LaunchTarget))
        {
            return 100;
        }

        if (!string.IsNullOrWhiteSpace(
                application.ResolvedExecutablePath)
            && File.Exists(application.ResolvedExecutablePath)
            && (string.IsNullOrWhiteSpace(application.Arguments)
                || (!application.Arguments.Contains(
                        "http://",
                        StringComparison.OrdinalIgnoreCase)
                    && !application.Arguments.Contains(
                        "https://",
                        StringComparison.OrdinalIgnoreCase))))
        {
            return 90;
        }

        var sourceExtension = Path.GetExtension(
            application.SourcePath
            ?? application.LaunchTarget);
        if (sourceExtension.Equals(
                ".url",
                StringComparison.OrdinalIgnoreCase)
            || sourceExtension.Equals(
                ".appref-ms",
                StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (!string.IsNullOrWhiteSpace(application.SourcePath)
            && File.Exists(application.SourcePath))
        {
            return 70;
        }

        return application.LaunchTarget.StartsWith(
            "shell:",
            StringComparison.OrdinalIgnoreCase)
            ? 60
            : 50;
    }

    private static long? TryGetFileLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ShouldIncludeApplication(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string[] excludedFragments =
        [
            "uninstall",
            "деинсталл",
            "удалить",
            "readme",
            "справка",
            "help",
            "лицензия",
            "license",
            "website",
            "веб-сайт"
        ];

        return !excludedFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool ShouldShowByDefault(FileSystemInfo item)
    {
        if (item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var attributes = item.Attributes;
        return !attributes.HasFlag(FileAttributes.System)
            && (ShowHiddenItems
                || !attributes.HasFlag(FileAttributes.Hidden));
    }

    private static bool LoadShowHiddenItems(string path)
    {
        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<FileViewPreferences>(
                    File.ReadAllText(path)) is { ShowHiddenItems: true };
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return false;
        }
    }

    private sealed record FileViewPreferences(bool ShowHiddenItems);

    private static FileSystemEntry CreateEntry(FileSystemInfo item)
    {
        var isDirectory = item is DirectoryInfo;
        var extension = isDirectory ? string.Empty : item.Extension.ToLowerInvariant();
        long? size = item is FileInfo file ? file.Length : null;

        return new FileSystemEntry(
            item.Name,
            item.FullName,
            isDirectory,
            item.LastWriteTimeUtc,
            size,
            isDirectory ? "Папка" : GetDisplayType(extension),
            isDirectory ? FolderGlyph : GetGlyph(extension),
            ShellIconCache.TryGetVisualPath(item.FullName));
    }

    private static string GetDisplayType(string extension) => extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "Изображение",
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "Видео",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => "Аудио",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Архив",
        ".exe" or ".msi" or ".msix" => "Приложение",
        ".torrent" => "Торрент-файл",
        ".doc" or ".docx" => "Документ Word",
        ".xls" or ".xlsx" => "Таблица Excel",
        ".ppt" or ".pptx" => "Презентация",
        ".pdf" => "Документ PDF",
        ".txt" or ".md" or ".rtf" => "Текстовый документ",
        "" => "Файл",
        _ => $"{extension.TrimStart('.').ToUpperInvariant()}-файл"
    };

    private static string GetGlyph(string extension) => extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => ImageGlyph,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => VideoGlyph,
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => MusicGlyph,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => ArchiveGlyph,
        ".exe" or ".msi" or ".msix" => ApplicationGlyph,
        ".torrent" => TorrentGlyph,
        ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or ".txt" or ".md" or ".rtf" => DocumentGlyph,
        _ => FileGlyph
    };

}

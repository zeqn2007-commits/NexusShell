using Nexus.Core.Models;

namespace Nexus.Core.Services;

public sealed class OrganizationService : IOrganizationService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm"
    };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz"
    };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix"
    };

    private readonly string _userProfile;
    private readonly string _downloads;
    private readonly string _documents;
    private readonly string _pictures;
    private readonly string _videos;
    private readonly string _music;

    public OrganizationService(string? userProfile = null)
    {
        _userProfile = Path.GetFullPath(
            userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _downloads = Path.Combine(_userProfile, "Downloads");
        _documents = ResolveKnownFolder(
            userProfile,
            Environment.SpecialFolder.MyDocuments,
            "Documents");
        _pictures = ResolveKnownFolder(
            userProfile,
            Environment.SpecialFolder.MyPictures,
            "Pictures");
        _videos = ResolveKnownFolder(
            userProfile,
            Environment.SpecialFolder.MyVideos,
            "Videos");
        _music = ResolveKnownFolder(
            userProfile,
            Environment.SpecialFolder.MyMusic,
            "Music");
    }

    public OrganizationSuggestion? Suggest(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var source = Path.GetFullPath(filePath);
        if (!File.Exists(source) || !IsDirectChildOf(source, _downloads))
        {
            return null;
        }

        var extension = Path.GetExtension(source);
        var fileName = Path.GetFileNameWithoutExtension(source);

        if (ImageExtensions.Contains(extension))
        {
            if (fileName.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("снимок экрана", StringComparison.OrdinalIgnoreCase))
            {
                var modified = File.GetLastWriteTime(source);
                var destination = Path.Combine(
                    _pictures,
                    "Скриншоты",
                    modified.Year.ToString(),
                    modified.Month.ToString("00"));

                return Create(
                    source,
                    destination,
                    $"Изображения › Скриншоты › {modified:yyyy} › {modified:MM}",
                    "Имя и формат соответствуют снимку экрана.",
                    98);
            }

            return Create(
                source,
                _pictures,
                "Изображения",
                "Формат файла относится к изображениям.",
                96);
        }

        if (VideoExtensions.Contains(extension))
        {
            return Create(source, _videos, "Медиа › Видео", "Формат файла относится к видео.", 97);
        }

        if (MusicExtensions.Contains(extension))
        {
            return Create(source, _music, "Медиа › Музыка", "Формат файла относится к аудио.", 97);
        }

        if (InstallerExtensions.Contains(extension))
        {
            return Create(
                source,
                Path.Combine(_downloads, "Установщики"),
                "Загрузки › Установщики",
                "Это установочный пакет Windows.",
                99);
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return Create(
                source,
                Path.Combine(_downloads, "Архивы"),
                "Загрузки › Архивы",
                "Формат файла относится к архивам.",
                99);
        }

        if (extension.Equals(".apk", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                source,
                Path.Combine(_documents, "Приложения", "Android"),
                "Приложения › Android",
                "Это установочный пакет Android.",
                99);
        }

        if (DocumentExtensions.Contains(extension))
        {
            return Create(
                source,
                _documents,
                "Документы",
                "Формат файла относится к документам.",
                93);
        }

        return null;
    }

    public Task<OrganizationResult> ApplyAsync(
        OrganizationSuggestion suggestion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = Path.GetFullPath(suggestion.SourcePath);
            var destinationDirectory = Path.GetFullPath(suggestion.DestinationDirectory);

            if (!File.Exists(source))
            {
                return new OrganizationResult(false, "Исходный файл больше не существует.");
            }

            if (!IsDirectChildOf(source, _downloads))
            {
                return new OrganizationResult(false, "Nexus перемещает только файлы из корня «Загрузок».");
            }

            if (!IsAllowedDestination(destinationDirectory))
            {
                return new OrganizationResult(false, "Папка назначения находится вне разрешённых пользовательских папок.");
            }

            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (File.Exists(destinationPath))
            {
                return new OrganizationResult(
                    false,
                    "В папке назначения уже есть файл с таким именем. Перемещение остановлено.");
            }

            Directory.CreateDirectory(destinationDirectory);
            File.Move(source, destinationPath);

            var operation = new OrganizationOperation(
                source,
                destinationPath,
                DateTimeOffset.Now);

            return new OrganizationResult(
                true,
                $"Файл перемещён в «{suggestion.DestinationLabel}».",
                operation);
        }, cancellationToken);
    }

    public Task<OrganizationResult> UndoAsync(
        OrganizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalPath = Path.GetFullPath(operation.SourcePath);
            var currentPath = Path.GetFullPath(operation.DestinationPath);

            if (!IsUnder(originalPath, _downloads)
                || !IsAllowedDestination(currentPath))
            {
                return new OrganizationResult(false, "Операция не прошла проверку безопасных путей.");
            }

            if (!File.Exists(currentPath))
            {
                return new OrganizationResult(false, "Перемещённый файл больше не найден.");
            }

            if (File.Exists(originalPath))
            {
                return new OrganizationResult(false, "В исходной папке уже есть файл с таким именем.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            File.Move(currentPath, originalPath);

            return new OrganizationResult(true, "Последнее перемещение отменено.");
        }, cancellationToken);
    }

    private static OrganizationSuggestion Create(
        string source,
        string destination,
        string label,
        string reason,
        int confidence)
    {
        return new OrganizationSuggestion(
            source,
            destination,
            label,
            reason,
            confidence);
    }

    private bool IsAllowedDestination(string destinationDirectory)
    {
        return IsUnder(destinationDirectory, _userProfile)
               || IsUnder(destinationDirectory, _documents)
               || IsUnder(destinationDirectory, _pictures)
               || IsUnder(destinationDirectory, _videos)
               || IsUnder(destinationDirectory, _music);
    }

    private string ResolveKnownFolder(
        string? explicitProfile,
        Environment.SpecialFolder folder,
        string fallbackName)
    {
        if (explicitProfile is not null)
        {
            return Path.Combine(_userProfile, fallbackName);
        }

        var resolved = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(resolved)
            ? Path.Combine(_userProfile, fallbackName)
            : Path.GetFullPath(resolved);
    }

    private static bool IsDirectChildOf(string filePath, string parentDirectory)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return directory is not null
            && string.Equals(
                directory.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

namespace Nexus.App.Models;

public enum FileKind
{
    Folder,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Image,
    Video,
    Audio,
    Archive,
    Code,
    Application,
    Text,
    Torrent,
    Drive,
    Computer,
    NetworkDevice,
    Other
}

public static class FileKinds
{
    public static FileKind FromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" => FileKind.Document,
        ".xls" or ".xlsx" or ".csv" or ".ods" => FileKind.Spreadsheet,
        ".ppt" or ".pptx" or ".odp" => FileKind.Presentation,
        ".pdf" => FileKind.Pdf,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" or ".fig" => FileKind.Image,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => FileKind.Video,
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => FileKind.Audio,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => FileKind.Archive,
        ".cs" or ".ts" or ".js" or ".py" or ".json" or ".xml" or ".xaml" or ".html" or ".css" or ".rs" or ".go" => FileKind.Code,
        ".exe" or ".msi" or ".msix" or ".lnk" or ".appx" => FileKind.Application,
        ".txt" or ".md" or ".log" or ".ini" => FileKind.Text,
        ".torrent" => FileKind.Torrent,
        _ => FileKind.Other
    };

    public static string TypeName(FileKind kind, string? extension)
    {
        var upper = extension?.TrimStart('.').ToUpperInvariant();
        return kind switch
        {
            FileKind.Folder => "Папка с файлами",
            FileKind.Document => "Документ Microsoft Word",
            FileKind.Spreadsheet => "Лист Microsoft Excel",
            FileKind.Presentation => "Презентация PowerPoint",
            FileKind.Pdf => "Документ PDF",
            FileKind.Image => $"Изображение {upper}",
            FileKind.Video => $"Видео {upper}",
            FileKind.Audio => $"Аудио {upper}",
            FileKind.Archive => $"Архив {upper}",
            FileKind.Code => $"Исходный код {upper}",
            FileKind.Application => "Приложение",
            FileKind.Text => "Текстовый документ",
            FileKind.Torrent => "Торрент-файл",
            FileKind.Drive => "Локальный диск",
            _ => string.IsNullOrEmpty(upper) ? "Файл" : $"Файл {upper}"
        };
    }
}

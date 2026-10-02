using Nexus.Core.Organizer;

namespace Nexus.App.Models;

/// <summary>A file of Downloads that can most likely go.</summary>
public sealed class CleanupItem(CleanupSuggestion suggestion)
{
    public CleanupSuggestion Suggestion { get; } = suggestion;

    public string Name => Suggestion.Item.Name;

    public string Path => Suggestion.Item.Path;

    public string Details => Suggestion.Reason switch
    {
        CleanupReason.InstalledProgram => $"«{Suggestion.Related}» уже установлена · {Formatting.Size(Suggestion.Item.Size)}",
        CleanupReason.ExtractedArchive => $"Рядом уже есть распакованная папка «{Suggestion.Related}» · {Formatting.Size(Suggestion.Item.Size)}",
        CleanupReason.Duplicate => $"Точная копия «{Suggestion.Related}» · {Formatting.Size(Suggestion.Item.Size)}",
        _ => $"Уже в {Suggestion.Related} · {Formatting.Size(Suggestion.Item.Size)}"
    };
}

/// <summary>Suggestions of one kind: installers of installed programs, extracted archives, copies, added torrents.</summary>
public sealed class CleanupGroup(CleanupReason reason, IReadOnlyList<CleanupItem> items)
{
    public CleanupReason Reason { get; } = reason;

    public IReadOnlyList<CleanupItem> Items { get; } = items;

    public long Size => Items.Sum(item => item.Suggestion.Item.Size);

    public string Title => Reason switch
    {
        CleanupReason.InstalledProgram => "Установщики уже установленных программ",
        CleanupReason.ExtractedArchive => "Архивы, которые уже распакованы",
        CleanupReason.Duplicate => "Копии файлов",
        _ => "Торрент-файлы, уже добавленные в клиент"
    };

    public string Glyph => Reason switch
    {
        CleanupReason.InstalledProgram => "",
        CleanupReason.ExtractedArchive => "",
        CleanupReason.Duplicate => "",
        _ => ""
    };

    public string Caption => $"{Formatting.Count(Items.Count, "файл", "файла", "файлов")} · {Formatting.Size(Size)}";

    public string DeleteAllText => Items.Count == 1 ? "Удалить" : $"Удалить все ({Items.Count})";
}

/// <summary>The files of one category and the folder inside Downloads they would go to.</summary>
public sealed class CategoryGroup(DownloadCategory category, IReadOnlyList<DownloadItem> items)
{
    public DownloadCategory Category { get; } = category;

    public IReadOnlyList<DownloadItem> Items { get; } = items;

    public string Title => DownloadsOrganizer.FolderNames[Category];

    public string Glyph => Category switch
    {
        DownloadCategory.Documents => "",
        DownloadCategory.Images => "",
        DownloadCategory.Video => "",
        DownloadCategory.Music => "",
        DownloadCategory.Archives => "",
        DownloadCategory.Programs => "",
        DownloadCategory.Torrents => "",
        _ => ""
    };

    public string Caption =>
        $"{Formatting.Count(Items.Count, "файл", "файла", "файлов")} · {Formatting.Size(Items.Sum(item => item.Size))} → Загрузки › {Title}";

    /// <summary>"отчёт.pdf, план.docx, счёт.pdf и ещё 4".</summary>
    public string Preview => string.Join(", ", Items.Take(3).Select(item => item.Name)) + (Items.Count > 3 ? $" и ещё {Items.Count - 3}" : string.Empty);
}

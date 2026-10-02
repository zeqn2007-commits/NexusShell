using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Nexus.App.Shell;
using Nexus.Core.Ai;
using Nexus.Core.Games;
using Nexus.Core.IO;
using Nexus.Core.Settings;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>A quick-access tile on the home page.</summary>
public sealed partial class QuickAccessItem(string title, string path, string subtitle, bool isPinned) : ObservableObject
{
    public string Title { get; } = title;

    public string Path { get; } = path;

    public string Subtitle { get; } = subtitle;

    public bool IsPinned { get; } = isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;
}

public sealed partial class HomeViewModel(SettingsStore settings, RecentItems recent, GameLibrary games, AiWorkspace ai) : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private bool _showingFavorites;

    public ObservableCollection<QuickAccessItem> QuickAccess { get; } = [];

    public ObservableCollection<DriveItem> Drives { get; } = [];

    /// <summary>"Продолжить играть": the last games played, newest first.</summary>
    public ObservableCollection<GameItem> RecentGames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecentGames))]
    public partial bool HasRecentGames { get; private set; }

    /// <summary>AI summary: "11 скиллов · 3 MCP-сервера", and the newest AI files found in the user's folders.</summary>
    [ObservableProperty]
    public partial string AiSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<AiFileItem> NewAiFiles { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiSummary))]
    public partial bool HasAiSummary { get; private set; }

    /// <summary>Blocks turned off in Настройки › Главная are neither shown nor loaded.</summary>
    public bool ShowRecentGames => HasRecentGames && IsShown("games");

    public bool ShowFiles => IsShown("files");

    public bool ShowDrives => IsShown("drives");

    public bool ShowAiSummary => HasAiSummary && IsShown("ai");

    [ObservableProperty]
    public partial bool HasNewAiFiles { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileListEmpty))]
    public partial IReadOnlyList<FileItemViewModel> FileList { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoadingFiles { get; private set; }

    public bool IsFileListEmpty => !IsLoadingFiles && FileList.Count == 0;

    public string FileListEmptyText => _showingFavorites
        ? "Добавьте файлы и папки в избранное через контекстное меню."
        : "Здесь появятся файлы, которые вы недавно открывали.";

    public string DateLine
    {
        get
        {
            var text = DateTime.Now.ToString("dddd, d MMMM", Russian);
            return char.ToUpper(text[0], Russian) + text[1..];
        }
    }

    public async Task LoadAsync()
    {
        LoadQuickAccess();
        await Task.WhenAll(
            ShowDrives ? LoadDrivesAsync() : Task.CompletedTask,
            ShowFiles ? LoadFileListAsync() : Task.CompletedTask,
            IsShown("games") ? LoadRecentGamesAsync() : Task.CompletedTask,
            IsShown("ai") ? LoadAiSummaryAsync() : Task.CompletedTask);
    }

    private bool IsShown(string section) => !settings.Current.HiddenHomeSections.Contains(section);

    public Task ShowRecentAsync()
    {
        _showingFavorites = false;
        OnPropertyChanged(nameof(FileListEmptyText));
        return LoadFileListAsync();
    }

    public Task ShowFavoritesAsync()
    {
        _showingFavorites = true;
        OnPropertyChanged(nameof(FileListEmptyText));
        return LoadFileListAsync();
    }

    public void Unpin(QuickAccessItem item)
    {
        settings.Update(s => s.PinnedFolders.RemoveAll(path => PathHelper.AreEqual(path, item.Path)));
        QuickAccess.Remove(item);
    }

    private void LoadQuickAccess()
    {
        QuickAccess.Clear();
        foreach (var folder in KnownFolders.UserFolders.Where(folder => folder.Folder != KnownFolder.Profile))
        {
            QuickAccess.Add(new QuickAccessItem(folder.Title, folder.Path, "Хранится локально", isPinned: false));
        }

        foreach (var path in settings.Current.PinnedFolders.Where(Directory.Exists))
        {
            var parent = PathHelper.GetParent(path);
            var subtitle = parent is null ? path : NavLocation.KnownFolderTitle(parent) ?? Path.GetFileName(parent);
            QuickAccess.Add(new QuickAccessItem(Path.GetFileName(path.TrimEnd('\\')), path, subtitle, isPinned: true));
        }
    }

    private async Task LoadDrivesAsync()
    {
        var drives = await Nexus.Core.IO.Drives.GetReadyAsync(includeNetwork: false);
        Drives.Clear();
        foreach (var drive in drives.Where(drive => drive.Type is DriveType.Fixed or DriveType.Removable))
        {
            Drives.Add(DriveItem.FromEntry(drive));
        }
    }

    private async Task LoadAiSummaryAsync()
    {
        var snapshot = await ai.GetAsync();
        var parts = new List<string>();
        if (snapshot.Models.Count > 0)
        {
            parts.Add(Formatting.Count(snapshot.Models.Count, "модель", "модели", "моделей"));
        }

        if (snapshot.Skills.Count > 0)
        {
            parts.Add(Formatting.Count(snapshot.Skills.Count, "скилл", "скилла", "скиллов"));
        }

        if (snapshot.McpServers.Count > 0)
        {
            parts.Add(Formatting.Count(snapshot.McpServers.Count, "MCP-сервер", "MCP-сервера", "MCP-серверов"));
        }

        if (snapshot.Projects.Count > 0)
        {
            parts.Add(Formatting.Count(snapshot.Projects.Count, "проект", "проекта", "проектов"));
        }

        AiSummary = string.Join(" · ", parts);
        NewAiFiles = snapshot.Files.Take(3).Select(file => new AiFileItem(file)).ToArray();
        HasNewAiFiles = NewAiFiles.Count > 0;
        HasAiSummary = parts.Count > 0 || HasNewAiFiles;
    }

    private async Task LoadRecentGamesAsync()
    {
        var library = await games.LoadAsync();
        RecentGames.Clear();
        foreach (var game in library.Where(game => game.LastPlayed is not null).OrderByDescending(game => game.LastPlayed).Take(4))
        {
            RecentGames.Add(GameItem.FromEntry(game));
        }

        HasRecentGames = RecentGames.Count > 0;
    }

    private async Task LoadFileListAsync()
    {
        IsLoadingFiles = true;
        OnPropertyChanged(nameof(IsFileListEmpty));
        try
        {
            var options = new DirectoryReadOptions(settings.Current.ShowHiddenItems);
            IReadOnlyList<FileEntry> entries = _showingFavorites
                ? await Task.Run(() => settings.Current.Favorites.Select(FileEntry.TryFromPath).OfType<FileEntry>().ToArray())
                : await recent.GetAsync(10, options);
            FileList = entries
                .Select(entry => new FileItemViewModel(entry, settings.Current.ShowFileExtensions, ShortLocation(entry.FullPath)))
                .ToArray();
        }
        finally
        {
            IsLoadingFiles = false;
            OnPropertyChanged(nameof(IsFileListEmpty));
        }
    }

    private static string ShortLocation(string path)
    {
        var parent = PathHelper.GetParent(path) ?? path;
        var known = KnownFolders.UserFolders.FirstOrDefault(folder =>
            folder.Folder != KnownFolder.Profile && (PathHelper.AreEqual(folder.Path, parent) || PathHelper.IsInside(parent, folder.Path)));
        if (known is null)
        {
            return parent;
        }

        var relative = Path.GetRelativePath(known.Path, parent);
        return relative == "." ? known.Title : $"{known.Title} › {relative.Replace("\\", " › ", StringComparison.Ordinal)}";
    }
}

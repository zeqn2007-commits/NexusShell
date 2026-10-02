using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.Apps;
using Nexus.Core.IO;
using Nexus.Core.Operations;
using Nexus.Core.Organizer;
using Nexus.Core.Settings;
using Nexus.Core.Shell;
using Nexus.Core.Torrents;

namespace Nexus.App.ViewModels;

/// <summary>
/// "Разбор «Загрузок»": suggests what can go (installers of installed programs, extracted archives, copies,
/// torrents a client already has) and sorts the rest into category folders inside Downloads. Nothing happens
/// without a click, and every change can be undone.
/// </summary>
public sealed partial class OrganizerViewModel(
    FileOperationService operations,
    UndoHistory undo,
    ShellViewModel shell,
    WindowContext window,
    SettingsStore settings,
    DialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesCount), nameof(FilesCaption), nameof(IsEmpty))]
    public partial IReadOnlyList<DownloadItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanupCount), nameof(CleanupCaption), nameof(HasCleanup))]
    public partial IReadOnlyList<CleanupGroup> CleanupGroups { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortCount), nameof(SortCaption), nameof(HasCategories))]
    public partial IReadOnlyList<CategoryGroup> Categories { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOthers), nameof(OthersText))]
    public partial IReadOnlyList<DownloadItem> Others { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public string DownloadsFolder
    {
        get
        {
#if DEBUG
            // Design reviews and manual tests work on a copy instead of the real Downloads.
            if (Environment.GetEnvironmentVariable("NEXUS_DOWNLOADS_FOLDER") is { Length: > 0 } demo)
            {
                return demo;
            }
#endif
            return KnownFolders.GetPath(KnownFolder.Downloads);
        }
    }

    public string FilesCount => Number(Items.Count);

    public string FilesCaption => Items.Count == 0
        ? "файлов"
        : $"{Formatting.Word(Items.Count, "файл", "файла", "файлов")} · {Formatting.Size(Items.Sum(item => item.Size))}";

    public string CleanupCount => Number(CleanupGroups.Sum(group => group.Items.Count));

    public string CleanupCaption
    {
        get
        {
            var count = CleanupGroups.Sum(group => group.Items.Count);
            return count == 0 ? "можно удалить" : $"можно удалить · {Formatting.Size(CleanupGroups.Sum(group => group.Size))}";
        }
    }

    public string SortCount => Number(Categories.Sum(group => group.Items.Count));

    public string SortCaption => Categories.Count == 0
        ? "разложить по папкам"
        : $"разложить по {Formatting.Count(Categories.Count, "папке", "папкам", "папкам")}";

    public bool HasCleanup => CleanupGroups.Count > 0;

    public bool HasCategories => Categories.Count > 0;

    public bool HasOthers => Others.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public string OthersText =>
        $"Останутся на месте ({Formatting.Count(Others.Count, "файл", "файла", "файлов")} неизвестного типа): " +
        string.Join(", ", Others.Take(5).Select(item => item.Name)) + (Others.Count > 5 ? $" и ещё {Others.Count - 5}" : string.Empty);

    public string StatusText => Items.Count == 0
        ? "В «Загрузках» нечего разбирать"
        : $"{Formatting.Count(Items.Count, "файл", "файла", "файлов")} в «Загрузках»";

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var downloads = DownloadsFolder;
            var startApps = await SafeStartAppsAsync();
            var (items, suggestions) = await Task.Run(() => Analyze(downloads, startApps));
            Items = items;
            CleanupGroups = suggestions
                .GroupBy(suggestion => suggestion.Reason)
                .OrderBy(group => group.Key)
                .Select(group => new CleanupGroup(group.Key, group.Select(suggestion => new CleanupItem(suggestion)).ToArray()))
                .ToArray();

            // What is suggested for deletion is not offered for sorting as well.
            var suggested = suggestions.Select(suggestion => suggestion.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rest = items.Where(item => !suggested.Contains(item.Path)).ToArray();
            Categories = rest
                .Where(item => item.Category != DownloadCategory.Other)
                .GroupBy(item => item.Category)
                .OrderBy(group => group.Key)
                .Select(group => new CategoryGroup(group.Key, group.ToArray()))
                .ToArray();
            Others = rest.Where(item => item.Category == DownloadCategory.Other).ToArray();
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task SortAsync(CategoryGroup group) => MoveAsync([group]);

    public Task SortAllAsync() => MoveAsync(Categories);

    public Task DeleteAsync(CleanupItem item) => RecycleAsync([item]);

    public Task DeleteGroupAsync(CleanupGroup group) => RecycleAsync(group.Items);

    public void Open(string path)
    {
        try
        {
            ShellLauncher.Open(path);
        }
        catch (Win32Exception exception)
        {
            shell.NotifyError($"Windows не смогла открыть «{Path.GetFileName(path)}»: {exception.Message}");
        }
    }

    public void ShowInFolder(string path)
    {
        if (PathHelper.GetParent(path) is { } folder)
        {
            shell.RequestSelection(path);
            shell.Navigate(NavLocation.ForFolder(folder));
        }
    }

    public void OpenDownloads() => shell.Navigate(NavLocation.ForFolder(DownloadsFolder));

    private static (DownloadItem[] Items, List<CleanupSuggestion> Suggestions) Analyze(string downloads, IReadOnlyList<string> startApps)
    {
        var items = DownloadsOrganizer.Scan(downloads, DateTimeOffset.Now).ToArray();
        var installed = InstalledPrograms.Load().Concat(startApps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var suggestions = new List<CleanupSuggestion>();
        suggestions.AddRange(DownloadsOrganizer.FindInstalledInstallers(items, installed));
        suggestions.AddRange(DownloadsOrganizer.FindExtractedArchives(items));
        suggestions.AddRange(DownloadsOrganizer.FindDuplicates(items));
        suggestions.AddRange(AddedTorrents(items));

        // One reason per file is enough: the first one found wins.
        var unique = suggestions.DistinctBy(suggestion => suggestion.Item.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return (items, unique);
    }

    /// <summary>.torrent files a client already has, matched by info hash as on the Torrents page.</summary>
    private static IEnumerable<CleanupSuggestion> AddedTorrents(IReadOnlyList<DownloadItem> items)
    {
        var torrents = items.Where(item => item.Category == DownloadCategory.Torrents).ToArray();
        if (torrents.Length == 0)
        {
            return [];
        }

        var downloads = TorrentLibrary.LoadDownloads(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var byHash = downloads
            .Where(download => download.InfoHash is not null)
            .GroupBy(download => download.InfoHash!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Client, StringComparer.OrdinalIgnoreCase);
        return torrents
            .Select(item => (Item: item, Hash: TorrentLibrary.ReadInfo(item.Path)?.InfoHash))
            .Where(entry => entry.Hash is not null && byHash.ContainsKey(entry.Hash))
            .Select(entry => new CleanupSuggestion(entry.Item, CleanupReason.TorrentAdded, byHash[entry.Hash!]));
    }

    private static async Task<IReadOnlyList<string>> SafeStartAppsAsync()
    {
        try
        {
            return (await StartApps.LoadAsync()).Select(app => app.Name).ToArray();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>Moves the groups into their folders inside Downloads; one undo puts everything back.</summary>
    private async Task MoveAsync(IReadOnlyList<CategoryGroup> groups)
    {
        var downloads = DownloadsFolder;
        var started = DateTimeOffset.Now;
        var moves = new List<(string From, string To)>();
        string? error = null;
        foreach (var group in groups)
        {
            var destination = DownloadsOrganizer.Destination(downloads, group.Category);
            try
            {
                Directory.CreateDirectory(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.Message;
                continue;
            }

            var sources = group.Items.Select(item => item.Path).ToArray();
            var outcome = await operations.MoveAsync(sources, destination, window.Handle);
            moves.AddRange(MovedAction.FromOutcome(sources, outcome.CreatedPaths, started).Moves);
            if (outcome.Cancelled)
            {
                break;
            }

            error ??= outcome.Error;
        }

        if (moves.Count > 0)
        {
            undo.Record(new MovedAction(moves, started));
            var folders = groups.Count == 1 ? $"в «Загрузки › {groups[0].Title}»" : $"по {Formatting.Count(groups.Count, "папке", "папкам", "папкам")}";
            shell.Notify($"Разложено {folders}: {Formatting.Count(moves.Count, "файл", "файла", "файлов")}.", InfoBarSeverity.Success,
                actionText: "Отменить", action: UndoAsync);
        }
        else if (error is not null)
        {
            shell.NotifyError(error);
        }

        await LoadAsync();
    }

    private async Task RecycleAsync(IReadOnlyList<CleanupItem> items)
    {
        var paths = items.Select(item => item.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (settings.Current.ConfirmRecycle && !await dialogs.ConfirmAsync(
                "Удалить в Корзину?",
                paths.Length == 1 ? $"«{items[0].Name}» будет перемещён в Корзину." : $"В Корзину будут перемещены {Formatting.Count(paths.Length, "файл", "файла", "файлов")}.",
                "Удалить"))
        {
            return;
        }

        var started = DateTimeOffset.Now;
        var outcome = await operations.RecycleAsync(paths, window.Handle);
        if (outcome.Error is not null && !outcome.Cancelled)
        {
            shell.NotifyError(outcome.Error);
        }
        else if (!outcome.Cancelled)
        {
            undo.Record(new RecycledAction(paths, started));
            shell.Notify(
                paths.Length == 1 ? $"«{items[0].Name}» перемещён в Корзину." : $"В Корзину: {Formatting.Count(paths.Length, "файл", "файла", "файлов")}.",
                InfoBarSeverity.Success,
                actionText: "Отменить",
                action: UndoAsync);
        }

        await LoadAsync();
    }

    private async Task UndoAsync()
    {
        try
        {
            shell.Notify(await undo.UndoLastAsync(window.Handle));
            DownloadsOrganizer.RemoveEmptyCategoryFolders(DownloadsFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            shell.NotifyError(exception.Message, "Не удалось отменить");
        }

        await LoadAsync();
    }

    private static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
}

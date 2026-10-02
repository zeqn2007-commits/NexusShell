using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.IO;
using Nexus.Core.Operations;
using Nexus.Core.Shell;
using Nexus.Core.Torrents;

namespace Nexus.App.ViewModels;

/// <summary>
/// "Торренты": what the torrent clients download, and the .torrent files in Downloads and
/// on the desktop. Files a client already has (matched by info hash) are marked so they can be cleared away.
/// </summary>
public sealed partial class TorrentsViewModel(
    FileOperationService operations,
    UndoHistory undo,
    ShellViewModel shell,
    WindowContext window) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsCount), nameof(DownloadsCaption), nameof(CompletedCount), nameof(CompletedCaption),
        nameof(ActiveCount), nameof(ActiveCaption), nameof(HasDownloads), nameof(StatusText))]
    public partial IReadOnlyList<TorrentDownloadItem> Downloads { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesCount), nameof(FilesCaption), nameof(HasFiles), nameof(AddedFiles), nameof(HasAddedFiles),
        nameof(CleanupText), nameof(StatusText))]
    public partial IReadOnlyList<TorrentFileItem> Files { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClient), nameof(OpenClientText))]
    public partial DefaultApp? Client { get; private set; }

    [ObservableProperty]
    public partial ImageSource? ClientIcon { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public string DownloadsCount => Number(Downloads.Count);

    public string DownloadsCaption => Downloads.Count == 0
        ? "загрузок"
        : $"{Formatting.Word(Downloads.Count, "загрузка", "загрузки", "загрузок")} · {Formatting.Size(Downloads.Sum(item => item.Download.Size ?? 0))}";

    public string CompletedCount => Number(Downloads.Count(item => item.IsComplete));

    public string CompletedCaption => Formatting.Word(Downloads.Count(item => item.IsComplete), "завершена", "завершены", "завершено");

    public string ActiveCount => Number(Downloads.Count(item => item.IsDownloading));

    public string ActiveCaption => Formatting.Word(Downloads.Count(item => item.IsDownloading), "загружается", "загружаются", "загружаются");

    public string FilesCount => Number(Files.Count);

    public string FilesCaption => Formatting.Word(Files.Count, "торрент-файл", "торрент-файла", "торрент-файлов");

    public bool HasDownloads => Downloads.Count > 0;

    public bool HasFiles => Files.Count > 0;

    public bool HasClient => Client is not null;

    public string OpenClientText => Client is null ? string.Empty : $"Открыть {Client.Name}";

    public IReadOnlyList<TorrentFileItem> AddedFiles => Files.Where(file => file.IsAdded).ToArray();

    public bool HasAddedFiles => Files.Any(file => file.IsAdded);

    public string CleanupText => $"Удалить уже добавленные ({AddedFiles.Count})";

    public string StatusText =>
        $"{Formatting.Count(Downloads.Count, "загрузка", "загрузки", "загрузок")} · {Formatting.Count(Files.Count, "торрент-файл", "торрент-файла", "торрент-файлов")}";

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var (downloads, files, client) = await Task.Run(Scan);
            Client = client;
            Downloads = downloads;
            Files = files;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>A folder download opens inside Nexus; a single-file one opens its folder with the file selected.</summary>
    public void Open(TorrentDownloadItem item)
    {
        if (item.Download.ContentPath is not { } path)
        {
            return;
        }

        if (item.IsFolder)
        {
            shell.Navigate(NavLocation.ForFolder(path));
        }
        else
        {
            ShowInFolder(path);
        }
    }

    public void OpenInNewTab(TorrentDownloadItem item)
    {
        if (item.Download.ContentPath is { } path && (item.IsFolder ? path : PathHelper.GetParent(path)) is { } folder)
        {
            shell.OpenInNewTab(NavLocation.ForFolder(folder));
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

    public void CopyPath(string path)
    {
        if (FileClipboard.SetText(path.Contains(' ') ? $"\"{path}\"" : path, window.Handle))
        {
            shell.Notify("Путь скопирован в буфер обмена.", InfoBarSeverity.Success);
        }
    }

    /// <summary>Hands a .torrent file to the default torrent client.</summary>
    public void OpenFile(TorrentFileItem item)
    {
        try
        {
            ShellLauncher.Open(item.File.Path);
        }
        catch (Win32Exception exception)
        {
            shell.NotifyError($"Windows не смогла открыть «{item.FileName}»: {exception.Message}");
        }
    }

    public void OpenClient()
    {
        if (Client is not { } client)
        {
            return;
        }

        try
        {
            ShellLauncher.Open(client.Executable);
        }
        catch (Win32Exception exception)
        {
            shell.NotifyError(exception.Message, $"{client.Name} не запустился");
        }
    }

    public Task DeleteAsync(TorrentFileItem item) => RecycleAsync([item]);

    /// <summary>Clients keep their own copy of every added .torrent, so the originals are safe to delete.</summary>
    public Task DeleteAddedAsync() => RecycleAsync(AddedFiles);

    private async Task RecycleAsync(IReadOnlyList<TorrentFileItem> items)
    {
        var paths = items.Select(item => item.File.Path).ToArray();
        if (paths.Length == 0)
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
                paths.Length == 1
                    ? $"«{items[0].FileName}» перемещён в Корзину."
                    : $"В Корзину: {Formatting.Count(paths.Length, "торрент-файл", "торрент-файла", "торрент-файлов")}.",
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            shell.NotifyError(exception.Message, "Не удалось отменить");
        }

        await LoadAsync();
    }

    private static (TorrentDownloadItem[] Downloads, TorrentFileItem[] Files, DefaultApp? Client) Scan()
    {
        var (appData, localAppData, folders) = Locations();
        var client = FileAssociations.GetDefaultApp(".torrent");
        var downloads = TorrentLibrary.LoadDownloads(appData, localAppData);
        var byHash = downloads
            .Where(download => download.InfoHash is not null)
            .GroupBy(download => download.InfoHash!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var downloadItems = downloads
            .OrderBy(download => download.IsComplete)
            .ThenByDescending(download => download.Added)
            .Select(download =>
            {
                var isFolder = download.ContentPath is { } path && Directory.Exists(path);
                var exists = isFolder || (download.ContentPath is { } file && File.Exists(file));
                return new TorrentDownloadItem(download, isFolder, exists);
            })
            .ToArray();
        var fileItems = TorrentLibrary.FindFiles(folders)
            .Select(file => new TorrentFileItem(file, file.InfoHash is { } hash ? byHash.GetValueOrDefault(hash) : null, client?.Name))
            .ToArray();
        return (downloadItems, fileItems, client);
    }

    /// <summary>Where clients keep their data and where .torrent files are looked for.</summary>
    private static (string AppData, string LocalAppData, string[] Folders) Locations()
    {
#if DEBUG
        // Design reviews and manual tests point the page at a fake profile instead of the real clients.
        if (Environment.GetEnvironmentVariable("NEXUS_TORRENTS_PROFILE") is { Length: > 0 } profile)
        {
            return (Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"),
                [Path.Combine(profile, "Downloads"), Path.Combine(profile, "Desktop")]);
        }
#endif
        return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            [KnownFolders.GetPath(KnownFolder.Downloads), KnownFolders.GetPath(KnownFolder.Desktop)]);
    }

    private static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
}

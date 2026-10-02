using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.IO;
using Nexus.Core.Operations;
using Nexus.Core.Settings;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>
/// A folder (or a virtual list such as Recent and Favorites) shown as a file table.
/// Owns loading, sorting, filtering, the command set and live refresh.
/// </summary>
public sealed partial class FolderViewModel : ObservableObject, IDisposable
{
    private const int SearchLimit = 5_000;

    private readonly SettingsStore _settings;
    private readonly FileOperationService _operations;
    private readonly UndoHistory _undo;
    private readonly RecentItems _recent;
    private readonly ShellViewModel _shell;
    private readonly WindowContext _window;
    private readonly DialogService _dialogs;
    private readonly List<FileItemViewModel> _all = [];
    private FolderWatcher? _watcher;
    private CancellationTokenSource? _loadCancellation;
    private string _filter = string.Empty;
    private bool _disposed;

    public FolderViewModel(
        SettingsStore settings,
        FileOperationService operations,
        UndoHistory undo,
        RecentItems recent,
        ShellViewModel shell,
        WindowContext window,
        DialogService dialogs)
    {
        _settings = settings;
        _operations = operations;
        _undo = undo;
        _recent = recent;
        _shell = shell;
        _window = window;
        _dialogs = dialogs;
        IsDetailsPaneOpen = settings.Current.ShowDetailsPane;
        ViewMode = settings.Current.ViewMode;
    }

    /// <summary>The page selects/focuses these paths after a reload (new folder, paste, rename).</summary>
    public event EventHandler<IReadOnlyList<string>>? SelectRequested;

    /// <summary>The page starts inline renaming for this item.</summary>
    public event EventHandler<FileItemViewModel>? RenameRequested;

    public NavLocation Location { get; private set; } = NavLocation.Home;

    [ObservableProperty]
    public partial ObservableCollection<FileItemViewModel> Items { get; private set; } = [];

    public IReadOnlyList<FileItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedKind))]
    public partial FileItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsDetailsPaneOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTilesView), nameof(IsDetailsView))]
    public partial FolderViewMode ViewMode { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial string EmptyGlyph { get; private set; } = "\uE8B7";

    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchResults { get; private set; }

    public bool HasSelection => SelectedItem is not null;

    public FileKind SelectedKind => SelectedItem?.Kind ?? FileKind.Other;

    public bool IsTilesView => ViewMode == FolderViewMode.Tiles;

    public bool IsDetailsView => ViewMode == FolderViewMode.Details;

    /// <summary>A real folder that can hold new items (a network computer only lists its shares).</summary>
    public bool IsPhysicalFolder => Location.Kind == PageKind.Folder && Location.Path is { } path && !PathHelper.IsNetworkComputer(path);

    public bool ShowsLocations => IsSearchResults || Location.Kind is PageKind.Recent or PageKind.Favorites;

    public string ThirdColumnHeader => ShowsLocations ? "Расположение" : "Тип";

    public SortField SortField => _settings.Current.SortField;

    public bool SortDescending => _settings.Current.SortDescending;

    public bool ShowHiddenItems => _settings.Current.ShowHiddenItems;

    public bool ShowFileExtensions => _settings.Current.ShowFileExtensions;

    public bool CanPaste => IsPhysicalFolder && FileClipboard.HasFiles();

    public string? CurrentFolder => IsPhysicalFolder ? Location.Path : null;

    partial void OnIsDetailsPaneOpenChanged(bool value) => _settings.Update(s => s.ShowDetailsPane = value);

    partial void OnViewModeChanged(FolderViewMode value) => _settings.Update(s => s.ViewMode = value);

    public async Task LoadAsync(NavLocation location)
    {
        Location = location;
        IsSearchResults = false;
        _filter = string.Empty;
        OnPropertyChanged(nameof(IsPhysicalFolder));
        OnPropertyChanged(nameof(ShowsLocations));
        OnPropertyChanged(nameof(ThirdColumnHeader));
        ConfigureWatcher();
        _all.Clear();
        Items = [];
        var pending = _shell.TakePendingSelection(location.Path);
        await ReloadAsync(pending is null ? null : [pending]);
    }

    /// <summary>
    /// Re-reads the current location. Unchanged items keep their view models, so icons,
    /// keyboard focus and scroll position survive refreshes caused by file changes.
    /// </summary>
    public async Task ReloadAsync(IReadOnlyList<string>? select = null)
    {
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var previousSelection = select ?? SelectedItems.Select(item => item.Path).ToArray();
        IsLoading = true;
        try
        {
            var entries = await ReadEntriesAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var showExtensions = _settings.Current.ShowFileExtensions;
            var existing = _all
                .Where(item => item.ShowsExtension == showExtensions)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _all.Clear();
            foreach (var (entry, location, kind) in entries)
            {
                _all.Add(existing.TryGetValue(entry.FullPath, out var current) && current.Entry == entry
                    ? current
                    : new FileItemViewModel(entry, showExtensions, location, kind));
            }

            ApplyView(incremental: true);
            UpdateEmptyState();
            if (previousSelection.Count > 0)
            {
                SelectRequested?.Invoke(this, previousSelection);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _all.Clear();
            Items.Clear();
            ShowEmpty("\uE7BA", exception is UnauthorizedAccessException ? "Нет доступа к папке" : "Папка недоступна",
                exception is UnauthorizedAccessException
                    ? "Windows не разрешает читать эту папку. Попробуйте открыть её в Проводнике."
                    : exception.Message);
        }
        finally
        {
            if (_loadCancellation == cancellation)
            {
                IsLoading = false;
            }

            UpdateStatus();
        }
    }

    private async Task<IReadOnlyList<(FileEntry Entry, string? Location, FileKind? Kind)>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions;
        switch (Location.Kind)
        {
            case PageKind.Folder when Location.Path is { } computer && PathHelper.IsNetworkComputer(computer):
                var shares = await NetworkShares.ReadAsync(computer, cancellationToken);
                return shares.Select(entry => (entry, (string?)null, (FileKind?)null)).ToArray();

            case PageKind.Folder when Location.Path is not null:
                var entries = await DirectoryReader.ReadAsync(Location.Path, options, cancellationToken);
                return entries.Select(entry => (entry, (string?)null, (FileKind?)null)).ToArray();

            case PageKind.Recent:
                var recent = await _recent.GetAsync(80, options, cancellationToken);
                return recent.Select(entry => (entry, LocationOf(entry.FullPath), (FileKind?)null)).ToArray();

            case PageKind.Favorites:
                var favorites = await Task.Run(() => _settings.Current.Favorites
                    .Select(FileEntry.TryFromPath)
                    .OfType<FileEntry>()
                    .ToArray(), cancellationToken);
                return favorites.Select(entry => (entry, LocationOf(entry.FullPath), (FileKind?)null)).ToArray();

            default:
                return [];
        }
    }

    private DirectoryReadOptions ReadOptions => new(_settings.Current.ShowHiddenItems);

    private static string? LocationOf(string path)
    {
        var parent = PathHelper.GetParent(path);
        if (parent is null)
        {
            return null;
        }

        var known = KnownFolders.UserFolders.FirstOrDefault(folder =>
            PathHelper.AreEqual(folder.Path, parent) || PathHelper.IsInside(parent, folder.Path));
        if (known is null || known.Folder == KnownFolder.Profile)
        {
            return parent;
        }

        return Breadcrumb(known.Title, known.Path, parent);
    }

    /// <summary>
    /// Search results are located relative to the folder being searched: "src › Core",
    /// or the folder's own name for direct children.
    /// </summary>
    private string? SearchLocationOf(string path)
    {
        var parent = PathHelper.GetParent(path);
        if (parent is null || Location.Path is null)
        {
            return parent;
        }

        var relative = Path.GetRelativePath(Location.Path, parent);
        return relative == "." ? Location.Title : relative.Replace("\\", " › ", StringComparison.Ordinal);
    }

    private static string Breadcrumb(string rootTitle, string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? rootTitle : $"{rootTitle} › {relative.Replace("\\", " › ", StringComparison.Ordinal)}";
    }

    public void SetSelection(IReadOnlyList<FileItemViewModel> selection)
    {
        SelectedItems = selection;
        SelectedItem = selection.Count > 0 ? selection[^1] : null;
        UpdateStatus();
    }

    public void SetSort(SortField field)
    {
        var descending = field == _settings.Current.SortField && !_settings.Current.SortDescending;
        _settings.Update(s =>
        {
            s.SortField = field;
            s.SortDescending = descending;
        });
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDescending));
        ApplyView();
    }

    public void SetSortDirection(bool descending)
    {
        _settings.Update(s => s.SortDescending = descending);
        OnPropertyChanged(nameof(SortDescending));
        ApplyView();
    }

    public void SetSortField(SortField field)
    {
        _settings.Update(s => s.SortField = field);
        OnPropertyChanged(nameof(SortField));
        ApplyView();
    }

    public async Task SetShowHiddenAsync(bool value)
    {
        _settings.Update(s => s.ShowHiddenItems = value);
        OnPropertyChanged(nameof(ShowHiddenItems));
        await ReloadAsync();
    }

    public async Task SetShowExtensionsAsync(bool value)
    {
        _settings.Update(s => s.ShowFileExtensions = value);
        OnPropertyChanged(nameof(ShowFileExtensions));
        await ReloadAsync();
    }

    public void Filter(string text)
    {
        _filter = text.Trim();
        if (IsSearchResults && _filter.Length == 0)
        {
            IsSearchResults = false;
            OnPropertyChanged(nameof(ShowsLocations));
            OnPropertyChanged(nameof(ThirdColumnHeader));
            _ = ReloadAsync();
            return;
        }

        ApplyView(incremental: true);
        UpdateEmptyState();
        UpdateStatus();
    }

    /// <summary>Deep search below the current folder; results stream in as they are found.</summary>
    public async Task SearchAsync(string query)
    {
        if (!IsPhysicalFolder || string.IsNullOrWhiteSpace(query))
        {
            Filter(query);
            return;
        }

        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _filter = string.Empty;
        IsSearchResults = true;
        OnPropertyChanged(nameof(ShowsLocations));
        OnPropertyChanged(nameof(ThirdColumnHeader));
        _all.Clear();
        Items = [];
        IsEmpty = false;
        IsLoading = true;
        _shell.StatusText = $"Поиск «{query}»…";
        var showExtensions = _settings.Current.ShowFileExtensions;
        var batch = new List<FileItemViewModel>();
        try
        {
            await foreach (var entry in FileSearch.SearchAsync(Location.Path!, query, ReadOptions, SearchLimit, cancellation.Token))
            {
                var item = new FileItemViewModel(entry, showExtensions, SearchLocationOf(entry.FullPath));
                _all.Add(item);
                batch.Add(item);
                if (batch.Count >= 64)
                {
                    batch.ForEach(Items.Add);
                    batch.Clear();
                    _shell.StatusText = $"Найдено: {Formatting.Items(_all.Count)}…";
                }
            }

            batch.ForEach(Items.Add);
            ApplyView();
            if (_all.Count == 0)
            {
                ShowEmpty("\uE721", "Ничего не найдено", $"В папке «{Location.Title}» и вложенных папках нет элементов с «{query}» в имени.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_loadCancellation == cancellation && !_disposed)
            {
                IsLoading = false;
                _shell.StatusText = _all.Count >= SearchLimit
                    ? $"Показаны первые {Formatting.Items(SearchLimit)}"
                    : $"Найдено: {Formatting.Items(_all.Count)}";
            }
        }
    }

    private void ApplyView(bool incremental = false)
    {
        IEnumerable<FileItemViewModel> query = _all;
        if (_filter.Length > 0)
        {
            var terms = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(item => terms.All(term => item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
        }

        var target = Sort(query, _settings.Current.SortField, _settings.Current.SortDescending).ToList();
        if (!incremental || Items.Count == 0 || !TrySynchronize(Items, target))
        {
            Items = new ObservableCollection<FileItemViewModel>(target);
        }
    }

    /// <summary>Applies the difference in place; gives up (returns false) when too much changed.</summary>
    internal static bool TrySynchronize(ObservableCollection<FileItemViewModel> items, IReadOnlyList<FileItemViewModel> target)
    {
        const int MaximumEdits = 256;
        var wanted = new HashSet<FileItemViewModel>(target);
        var removals = items.Count(item => !wanted.Contains(item));
        var present = new HashSet<FileItemViewModel>(items);
        var additions = target.Count(item => !present.Contains(item));
        if (removals + additions > MaximumEdits)
        {
            return false;
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(items[index]))
            {
                items.RemoveAt(index);
            }
        }

        for (var index = 0; index < target.Count; index++)
        {
            if (index < items.Count && ReferenceEquals(items[index], target[index]))
            {
                continue;
            }

            var current = items.IndexOf(target[index]);
            if (current >= 0)
            {
                items.Move(current, index);
            }
            else
            {
                items.Insert(index, target[index]);
            }
        }

        return true;
    }

    internal static IEnumerable<FileItemViewModel> Sort(IEnumerable<FileItemViewModel> items, SortField field, bool descending)
    {
        var comparer = NaturalStringComparer.Instance;
        Comparison<FileItemViewModel> compare = field switch
        {
            SortField.Modified => (a, b) => a.Entry.Modified.CompareTo(b.Entry.Modified),
            SortField.Type => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            SortField.Size => (a, b) => a.Entry.Size.CompareTo(b.Entry.Size),
            _ => (a, b) => comparer.Compare(a.Name, b.Name)
        };

        var list = items.ToList();
        list.Sort((a, b) =>
        {
            // Folders always stay above files, like Explorer's default grouping.
            if (a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            var result = compare(a, b);
            if (result == 0)
            {
                result = comparer.Compare(a.Name, b.Name);
            }

            return descending ? -result : result;
        });
        return list;
    }

    private void UpdateEmptyState()
    {
        if (Items.Count > 0)
        {
            IsEmpty = false;
            return;
        }

        if (_filter.Length > 0)
        {
            ShowEmpty("\uE721", "Нет совпадений", $"Нажмите Enter, чтобы искать «{_filter}» во вложенных папках.");
            return;
        }

        switch (Location.Kind)
        {
            case PageKind.Folder when Location.Path is { } computer && PathHelper.IsNetworkComputer(computer):
                ShowEmpty("\uE977", "Нет общих папок", $"На компьютере «{Location.Title}» нет папок с общим доступом, которые вам разрешено открыть.");
                break;
            case PageKind.Folder:
                ShowEmpty("\uE8B7", "Эта папка пуста", "Перетащите сюда файлы или создайте новую папку.");
                break;
            case PageKind.Recent:
                ShowEmpty("\uE81C", "Недавних файлов нет", "Здесь появятся файлы, которые вы открывали в Windows.");
                break;
            case PageKind.Favorites:
                ShowEmpty("\uE734", "В избранном пока пусто", "Выберите файл или папку и нажмите «Добавить в избранное» в меню.");
                break;
            default:
                ShowEmpty("\uE946", "Раздел скоро появится", "Этот раздел подключается на одном из следующих этапов.");
                break;
        }
    }

    private void ShowEmpty(string glyph, string title, string message)
    {
        EmptyGlyph = glyph;
        EmptyTitle = title;
        EmptyMessage = message;
        IsEmpty = true;
    }

    private void UpdateStatus()
    {
        // A load that finishes after the user left the page must not overwrite the new page's status.
        if (_disposed)
        {
            return;
        }

        if (IsLoading && !IsSearchResults)
        {
            _shell.StatusText = "Загрузка…";
        }
        else if (!IsSearchResults)
        {
            _shell.StatusText = Formatting.Items(Items.Count);
        }

        if (SelectedItems.Count == 0)
        {
            _shell.SelectionText = string.Empty;
            return;
        }

        var files = SelectedItems.Where(item => !item.IsFolder).ToArray();
        var size = files.Length == SelectedItems.Count && files.Length > 0 ? $": {Formatting.Size(files.Sum(item => item.Entry.Size))}" : string.Empty;
        _shell.SelectionText = SelectedItems.Count == 1
            ? $"Выбран 1 элемент{size}"
            : $"Выбрано {Formatting.Items(SelectedItems.Count)}{size}";
    }

    private void ConfigureWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!IsPhysicalFolder)
        {
            return;
        }

        _watcher = FolderWatcher.TryCreate(Location.Path!);
        if (_watcher is not null)
        {
            _watcher.Changed += (_, _) => _window.Post(async () =>
            {
                if (!_disposed && !IsSearchResults && !Items.Any(item => item.IsRenaming))
                {
                    await ReloadAsync();
                }
            });
        }
    }

    // ---------- Commands ----------

    public async Task OpenAsync(FileItemViewModel? item = null)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        if (item.IsFolder)
        {
            _shell.Navigate(NavLocation.ForFolder(item.Path));
            return;
        }

        try
        {
            await Task.Run(() => ShellLauncher.Open(item.Path));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            _shell.NotifyError($"Windows не смогла открыть «{item.Name}»: {exception.Message}");
        }
    }

    public void OpenInNewTab(FileItemViewModel? item = null)
    {
        item ??= SelectedItem;
        if (item is { IsFolder: true })
        {
            _shell.OpenInNewTab(NavLocation.ForFolder(item.Path));
        }
    }

    public void OpenWith()
    {
        if (SelectedItem is { IsFolder: false } item)
        {
            TryShell(() => ShellLauncher.OpenWith(item.Path, _window.Handle), "Не удалось открыть выбор приложения.");
        }
    }

    public void ShowProperties()
    {
        var path = SelectedItem?.Path ?? CurrentFolder;
        if (path is not null)
        {
            TryShell(() => ShellLauncher.ShowProperties(path, _window.Handle), "Не удалось открыть свойства.");
        }
    }

    public void ShowInExplorer()
    {
        var path = SelectedItem?.Path ?? CurrentFolder;
        if (path is not null)
        {
            TryShell(() => ShellLauncher.ShowInExplorer(path), "Не удалось открыть Проводник.");
        }
    }

    public void OpenTerminal()
    {
        var folder = SelectedItem is { IsFolder: true } item ? item.Path : CurrentFolder;
        if (folder is not null)
        {
            TryShell(() => ShellLauncher.OpenTerminal(folder), "Не удалось открыть терминал.");
        }
    }

    public void CopyPath()
    {
        var paths = SelectedItems.Count > 0 ? SelectedItems.Select(item => item.Path) : CurrentFolder is { } folder ? [folder] : [];
        var text = string.Join(Environment.NewLine, paths.Select(path => path.Contains(' ') ? $"\"{path}\"" : path));
        if (text.Length > 0 && FileClipboard.SetText(text, _window.Handle))
        {
            _shell.Notify("Путь скопирован в буфер обмена.", InfoBarSeverity.Success);
        }
    }

    public void SetClipboard(ClipboardEffect effect)
    {
        var items = SelectedItems.Where(item => item.Kind != FileKind.Drive).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        if (!FileClipboard.SetFiles(items.Select(item => item.Path).ToArray(), effect, _window.Handle))
        {
            _shell.NotifyError("Буфер обмена занят другой программой. Попробуйте ещё раз.");
            return;
        }

        foreach (var item in _all)
        {
            item.IsCut = effect == ClipboardEffect.Move && items.Contains(item);
        }

        _shell.Notify(effect == ClipboardEffect.Move
            ? $"Вырезано: {Formatting.Items(items.Length)}. Откройте папку и нажмите Ctrl+V."
            : $"Скопировано: {Formatting.Items(items.Length)}.", InfoBarSeverity.Informational);
        OnPropertyChanged(nameof(CanPaste));
    }

    public async Task PasteAsync()
    {
        if (CurrentFolder is not { } destination)
        {
            return;
        }

        var clipboard = FileClipboard.GetFiles(_window.Handle);
        if (clipboard is null || clipboard.Paths.Count == 0)
        {
            _shell.Notify("В буфере обмена нет файлов.");
            return;
        }

        await TransferAsync(clipboard.Paths, destination, clipboard.Effect == ClipboardEffect.Move);
        if (clipboard.Effect == ClipboardEffect.Move)
        {
            FileClipboard.Clear(_window.Handle);
            OnPropertyChanged(nameof(CanPaste));
        }
    }

    /// <summary>Copy or move into <paramref name="destination"/> (paste and drag-and-drop).</summary>
    public async Task TransferAsync(IReadOnlyList<string> sources, string destination, bool move)
    {
        var started = DateTimeOffset.Now;
        var outcome = move
            ? await _operations.MoveAsync(sources, destination, _window.Handle)
            : await _operations.CopyAsync(sources, destination, _window.Handle);

        if (outcome.CreatedPaths.Count > 0)
        {
            _undo.Record(move
                ? MovedAction.FromOutcome(sources, outcome.CreatedPaths, started)
                : new CopiedAction(outcome.CreatedPaths, started));
        }

        if (outcome.Error is not null && !outcome.Cancelled)
        {
            _shell.NotifyError(outcome.Error);
        }
        else if (outcome.CreatedPaths.Count > 0)
        {
            _shell.Notify($"{(move ? "Перемещено" : "Скопировано")}: {Formatting.Items(outcome.CreatedPaths.Count)}",
                InfoBarSeverity.Success, actionText: "Отменить", action: UndoAsync);
        }

        if (CurrentFolder is { } current && PathHelper.AreEqual(current, destination))
        {
            await ReloadAsync(select: outcome.CreatedPaths);
        }
    }

    public async Task DeleteAsync(bool permanently)
    {
        var items = SelectedItems.Where(item => item.Kind != FileKind.Drive).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var paths = items.Select(item => item.Path).ToArray();
        var started = DateTimeOffset.Now;
        var outcome = permanently
            ? await _operations.DeletePermanentlyAsync(paths, _window.Handle)
            : await _operations.RecycleAsync(paths, _window.Handle);

        if (outcome.Error is not null && !outcome.Cancelled)
        {
            _shell.NotifyError(outcome.Error);
        }
        else if (!outcome.Cancelled && !permanently)
        {
            _undo.Record(new RecycledAction(paths, started));
            _shell.Notify($"Перемещено в Корзину: {Formatting.Items(paths.Length)}", InfoBarSeverity.Success,
                actionText: "Отменить", action: UndoAsync);
        }

        await ReloadAsync();
    }

    public async Task CreateFolderAsync()
    {
        if (CurrentFolder is not { } folder)
        {
            return;
        }

        try
        {
            var path = FileOperationService.CreateFolder(folder, PathHelper.GetAvailableName(folder, "Новая папка"));
            _undo.Record(new CreatedAction(path, DateTimeOffset.Now));
            await ReloadAsync(select: [path]);
            if (_all.FirstOrDefault(item => PathHelper.AreEqual(item.Path, path)) is { } created)
            {
                BeginRename(created);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.NotifyError(exception.Message, "Не удалось создать папку");
        }
    }

    public async Task CreateTextFileAsync()
    {
        if (CurrentFolder is not { } folder)
        {
            return;
        }

        try
        {
            var path = FileOperationService.CreateEmptyFile(folder, PathHelper.GetAvailableName(folder, "Новый текстовый документ", ".txt"));
            _undo.Record(new CreatedAction(path, DateTimeOffset.Now));
            await ReloadAsync(select: [path]);
            if (_all.FirstOrDefault(item => PathHelper.AreEqual(item.Path, path)) is { } created)
            {
                BeginRename(created);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.NotifyError(exception.Message, "Не удалось создать файл");
        }
    }

    public void BeginRename(FileItemViewModel? item = null)
    {
        item ??= SelectedItem;
        if (item is null || item.Kind == FileKind.Drive || !IsPhysicalFolder && !ShowsLocations)
        {
            return;
        }

        foreach (var other in _all.Where(other => other.IsRenaming))
        {
            other.IsRenaming = false;
        }

        item.IsRenaming = true;
        RenameRequested?.Invoke(this, item);
    }

    public async Task CommitRenameAsync(FileItemViewModel item, string newName)
    {
        item.IsRenaming = false;
        newName = newName.Trim();
        if (newName.Length == 0 || string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        // When extensions are hidden the user edits only the stem; keep the original extension.
        if (!item.IsFolder && !_settings.Current.ShowFileExtensions && !string.IsNullOrEmpty(item.Entry.Extension)
            && !newName.EndsWith(item.Entry.Extension, StringComparison.OrdinalIgnoreCase))
        {
            newName += item.Entry.Extension;
        }

        if (!item.IsFolder && _settings.Current.ShowFileExtensions
            && !string.Equals(Path.GetExtension(newName), item.Entry.Extension, StringComparison.OrdinalIgnoreCase)
            && !await _dialogs.ConfirmAsync("Изменить расширение?",
                "После изменения расширения файл может стать недоступным. Вы действительно хотите изменить его?",
                "Да, изменить"))
        {
            return;
        }

        try
        {
            var renamed = await Task.Run(() => FileOperationService.Rename(item.Path, newName));
            _undo.Record(new RenamedAction(item.Path, renamed, DateTimeOffset.Now));
            await ReloadAsync(select: [renamed]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.NotifyError(exception.Message, "Не удалось переименовать");
        }
    }

    public void CancelRename(FileItemViewModel item) => item.IsRenaming = false;

    public bool IsFavorite(FileItemViewModel item) =>
        _settings.Current.Favorites.Contains(item.Path, StringComparer.OrdinalIgnoreCase);

    public async Task ToggleFavoriteAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var remove = IsFavorite(item);
        _settings.Update(settings =>
        {
            settings.Favorites.RemoveAll(path => PathHelper.AreEqual(path, item.Path));
            if (!remove)
            {
                settings.Favorites.Add(item.Path);
            }
        });
        _shell.Notify(remove ? "Удалено из избранного." : "Добавлено в избранное.", InfoBarSeverity.Success);
        if (Location.Kind == PageKind.Favorites)
        {
            await ReloadAsync();
        }
    }

    public void PinToQuickAccess()
    {
        var folder = SelectedItem is { IsFolder: true } item ? item.Path : CurrentFolder;
        if (folder is null || _settings.Current.PinnedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)
            || KnownFolders.Find(folder) is not null)
        {
            return;
        }

        _settings.Update(settings => settings.PinnedFolders.Add(folder));
        _shell.Notify($"«{Path.GetFileName(folder)}» закреплена на панели быстрого доступа.", InfoBarSeverity.Success);
    }

    public async Task UndoAsync()
    {
        try
        {
            var message = await _undo.UndoLastAsync(_window.Handle);
            _shell.Notify(message, InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            _shell.NotifyError(exception.Message, "Не удалось отменить");
        }

        await ReloadAsync();
    }

    private void TryShell(Action action, string error)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or System.Runtime.InteropServices.COMException)
        {
            _shell.NotifyError($"{error} {exception.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loadCancellation?.Cancel();
        _watcher?.Dispose();
    }
}

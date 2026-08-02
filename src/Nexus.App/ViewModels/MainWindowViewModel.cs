using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int RecursiveSearchResultLimit = 2_000;
    private static readonly string InterfacePreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexus Shell",
        "show-system-applications.flag");
    private static readonly string LegacyInterfacePreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusShell",
        "show-system-applications.flag");

    private readonly IFileSystemService _fileSystem;
    private readonly IGameLibraryService _gameLibrary;
    private readonly IFavoritesService _favorites;
    private readonly IAiWorkspaceService _aiWorkspace;
    private readonly List<FileSystemEntry> _allItems = [];
    private readonly List<GameEntry> _allGames = [];
    private readonly Stack<BrowserLocation> _backStack = [];
    private readonly Stack<BrowserLocation> _forwardStack = [];
    private readonly object _watchGate = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _watchDebounceCancellation;
    private FileSystemWatcher? _watcher;
    private BrowserLocation? _currentLocation;
    private string _currentTitle = "Главная";
    private string _currentPath = string.Empty;
    private string _statusText = "Готово";
    private string? _errorMessage;
    private string _searchQuery = string.Empty;
    private bool _isHome = true;
    private bool _isGames;
    private bool _isFavorites;
    private bool _isBusy;
    private FileSortField _sortField = FileSortField.Name;
    private bool _sortDescending;
    private long _navigationVersion;
    private bool _isRecursiveSearchResults;
    private bool _isSearchResultLimitReached;
    private bool _showSystemApplications;
    private bool _disposed;

    public MainWindowViewModel(
        IFileSystemService fileSystem,
        IGameLibraryService gameLibrary,
        IFavoritesService favorites,
        IAiWorkspaceService? aiWorkspace = null)
    {
        _fileSystem = fileSystem;
        _gameLibrary = gameLibrary;
        _favorites = favorites;
        _aiWorkspace = aiWorkspace ?? new AiWorkspaceService();
        _showSystemApplications = LoadShowSystemApplications();
        NavigationTargets = fileSystem.GetNavigationTargets();
    }

    public ObservableCollection<FileSystemEntry> Items { get; } =
        new BulkObservableCollection<FileSystemEntry>();

    public ObservableCollection<FileSystemEntry> RecentItems { get; } =
        new BulkObservableCollection<FileSystemEntry>();

    public ObservableCollection<GameEntry> Games { get; } =
        new BulkObservableCollection<GameEntry>();

    public IReadOnlyList<NavigationTarget> NavigationTargets { get; }

    public event EventHandler? ActiveFolderChangedExternally;

    public string CurrentTitle
    {
        get => _currentTitle;
        private set
        {
            if (SetProperty(ref _currentTitle, value))
            {
                OnPropertyChanged(nameof(DisplayAddress));
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(DisplayAddress));
            }
        }
    }

    public string DisplayAddress => string.IsNullOrWhiteSpace(CurrentPath)
        ? $"Nexus › {CurrentTitle}"
        : CurrentPath;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsHome
    {
        get => _isHome;
        private set
        {
            if (SetProperty(ref _isHome, value))
            {
                OnPropertyChanged(nameof(CanSearch));
                OnPropertyChanged(nameof(CanNavigateUp));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsGames
    {
        get => _isGames;
        private set => SetProperty(ref _isGames, value);
    }

    public bool IsFavorites
    {
        get => _isFavorites;
        private set => SetProperty(ref _isFavorites, value);
    }

    public bool CanSearch =>
        !IsHome
        || !string.Equals(
            CurrentTitle,
            "AI-центр",
            StringComparison.CurrentCultureIgnoreCase);

    public bool ShowHiddenItems => _fileSystem.ShowHiddenItems;

    public bool ShowSystemApplications => _showSystemApplications;

    public int HiddenSystemApplicationsCount =>
        CurrentKind == NavigationKind.Applications
            ? _allItems.Count(IsSystemApplication)
            : 0;

    public bool IsRecursiveSearchResults
    {
        get => _isRecursiveSearchResults;
        private set => SetProperty(ref _isRecursiveSearchResults, value);
    }

    public bool IsSearchResultLimitReached
    {
        get => _isSearchResultLimitReached;
        private set => SetProperty(ref _isSearchResultLimitReached, value);
    }

    public NavigationKind CurrentKind =>
        _currentLocation?.Kind ?? NavigationKind.Home;

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public bool CanNavigateUp =>
        _currentLocation is
            {
                Kind: NavigationKind.Folder
                    or NavigationKind.Archive
                    or NavigationKind.OptionalFolder,
                Path: not null
            } location
        && Directory.Exists(location.Path)
        && Directory.GetParent(location.Path) is not null;

    public FileSortField SortField => _sortField;

    public bool SortDescending => _sortDescending;

    public string SortLabel => _sortField switch
    {
        FileSortField.Name => _sortDescending ? "Имя: Я–А" : "Имя: А–Я",
        FileSortField.Modified => _sortDescending ? "Сначала новые" : "Сначала старые",
        FileSortField.Type => _sortDescending ? "Тип: Я–А" : "Тип: А–Я",
        FileSortField.Size => _sortDescending ? "Сначала крупные" : "Сначала мелкие",
        _ => "Сортировка"
    };

    public Task InitializeAsync() => NavigateToTargetAsync("projects");

    public BrowserSessionState? CaptureSession()
    {
        if (_currentLocation is null)
        {
            return null;
        }

        return new BrowserSessionState(
            ToState(_currentLocation),
            _backStack.Select(ToState).ToArray(),
            _forwardStack.Select(ToState).ToArray(),
            _sortField,
            _sortDescending,
            SearchQuery,
            IsRecursiveSearchResults);
    }

    public async Task<bool> RestoreSessionAsync(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        SetSort(state.SortField, state.SortDescending);
        var restored = await NavigateAsync(
            FromState(state.Current),
            addHistory: false);
        if (!restored)
        {
            return false;
        }

        _backStack.Clear();
        foreach (var location in state.Back.Reverse())
        {
            _backStack.Push(FromState(location));
        }

        _forwardStack.Clear();
        foreach (var location in state.Forward.Reverse())
        {
            _forwardStack.Push(FromState(location));
        }

        if (!string.IsNullOrWhiteSpace(state.SearchQuery))
        {
            if (state.IsRecursiveSearchResults)
            {
                await SearchCurrentFolderAsync(state.SearchQuery);
            }
            else
            {
                SearchQuery = state.SearchQuery;
            }
        }

        NotifyHistoryChanged();
        return true;
    }

    public async Task NavigateToTargetAsync(string id)
    {
        var target = NavigationTargets.FirstOrDefault(item => item.Id == id);
        if (target is null)
        {
            return;
        }

        switch (target.Kind)
        {
            case NavigationKind.Home:
                await NavigateDashboardAsync(target.Label);
                break;
            case NavigationKind.Computer:
                await NavigateComputerAsync();
                break;
            case NavigationKind.Network:
                await NavigateVirtualAsync(
                    NavigationKind.Network,
                    target.Label);
                break;
            case NavigationKind.Recent:
                await NavigateVirtualAsync(NavigationKind.Recent, target.Label);
                break;
            case NavigationKind.Favorites:
                await NavigateVirtualAsync(NavigationKind.Favorites, target.Label);
                break;
            case NavigationKind.Archive:
                if (target.Path is not null)
                {
                    await NavigateAsync(
                        new BrowserLocation(
                            NavigationKind.Archive,
                            target.Label,
                            target.Path),
                        addHistory: true);
                }
                break;
            case NavigationKind.Media:
                await NavigateVirtualAsync(NavigationKind.Media, target.Label);
                break;
            case NavigationKind.Games:
                await NavigateVirtualAsync(NavigationKind.Games, target.Label);
                break;
            case NavigationKind.Torrents:
                await NavigateVirtualAsync(NavigationKind.Torrents, target.Label);
                break;
            case NavigationKind.Applications:
                await NavigateVirtualAsync(NavigationKind.Applications, target.Label);
                break;
            case NavigationKind.AiProjects:
                await NavigateVirtualAsync(NavigationKind.AiProjects, target.Label);
                break;
            case NavigationKind.OptionalFolder when target.Path is not null:
                if (target.Id == "projects" && !Directory.Exists(target.Path))
                {
                    try
                    {
                        Directory.CreateDirectory(target.Path);
                    }
                    catch (Exception exception) when (
                        exception is IOException
                        or UnauthorizedAccessException)
                    {
                        ErrorMessage =
                            "Не удалось создать папку проектов. Проверьте доступ к Документам.";
                        StatusText = "Нет доступа";
                        break;
                    }
                }

                await NavigateAsync(
                    new BrowserLocation(
                        NavigationKind.OptionalFolder,
                        target.Label,
                        target.Path),
                    addHistory: true);
                break;
            case NavigationKind.Collection when target.Path is not null:
                await NavigateAsync(
                    new BrowserLocation(
                        NavigationKind.Collection,
                        target.Label,
                        target.Path),
                    addHistory: true);
                break;
            case NavigationKind.Folder when target.Path is not null:
                await NavigateFolderAsync(target.Path, target.Label);
                break;
        }
    }

    public Task NavigateFolderAsync(string path, string? title = null, bool addHistory = true)
    {
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var folderTitle = title;
        if (string.IsNullOrWhiteSpace(folderTitle))
        {
            folderTitle = new DirectoryInfo(path).Name;
            if (string.IsNullOrWhiteSpace(folderTitle))
            {
                folderTitle = path;
            }
        }

        return NavigateAsync(
            new BrowserLocation(NavigationKind.Folder, folderTitle, path),
            addHistory);
    }

    public Task NavigateComputerAsync(bool addHistory = true)
    {
        return NavigateAsync(
            new BrowserLocation(NavigationKind.Computer, "Этот компьютер", null),
            addHistory);
    }

    public Task NavigateHomeAsync(bool addHistory = true)
    {
        return NavigateDashboardAsync("Главная", addHistory);
    }

    public Task NavigateDashboardAsync(string title, bool addHistory = true)
    {
        return NavigateAsync(
            new BrowserLocation(NavigationKind.Home, title, null),
            addHistory);
    }

    public Task NavigateVirtualAsync(
        NavigationKind kind,
        string title,
        bool addHistory = true)
    {
        return NavigateAsync(
            new BrowserLocation(kind, title, null),
            addHistory);
    }

    public async Task GoBackAsync()
    {
        if (_backStack.TryPeek(out var location))
        {
            var previous = _currentLocation;
            if (await NavigateAsync(location, addHistory: false))
            {
                _backStack.Pop();
                if (previous is not null)
                {
                    _forwardStack.Push(previous);
                }

                NotifyHistoryChanged();
            }
        }
    }

    public async Task GoForwardAsync()
    {
        if (_forwardStack.TryPeek(out var location))
        {
            var previous = _currentLocation;
            if (await NavigateAsync(location, addHistory: false))
            {
                _forwardStack.Pop();
                if (previous is not null)
                {
                    _backStack.Push(previous);
                }

                NotifyHistoryChanged();
            }
        }
    }

    public Task GoUpAsync()
    {
        if (_currentLocation is not
            {
                Kind: NavigationKind.Folder
                    or NavigationKind.Archive
                    or NavigationKind.OptionalFolder,
                Path: not null
            } location)
        {
            return Task.CompletedTask;
        }

        var parent = Directory.GetParent(location.Path);
        return parent is null
            ? NavigateComputerAsync()
            : NavigateFolderAsync(parent.FullName);
    }

    public Task RefreshAsync()
    {
        if (_currentLocation?.Kind == NavigationKind.AiProjects)
        {
            _aiWorkspace.InvalidateCache();
        }

        return _currentLocation is null
            ? InitializeAsync()
            : NavigateAsync(_currentLocation, addHistory: false);
    }

    public async Task SearchCurrentFolderAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await RefreshAsync();
            return;
        }

        if (_currentLocation is not
            {
                Kind: NavigationKind.Folder
                    or NavigationKind.Archive
                    or NavigationKind.OptionalFolder,
                Path: not null
            } location
            || !Directory.Exists(location.Path))
        {
            SearchQuery = query;
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var token = _loadCancellation.Token;
        var navigationVersion = Interlocked.Increment(ref _navigationVersion);
        IsBusy = true;
        ErrorMessage = null;
        StatusText = $"Поиск «{query}»…";
        try
        {
            var results = await _fileSystem.SearchDirectoryAsync(
                location.Path,
                query,
                RecursiveSearchResultLimit,
                token);
            token.ThrowIfCancellationRequested();
            if (navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            _allItems.Clear();
            _allItems.AddRange(results);
            IsRecursiveSearchResults = true;
            IsSearchResultLimitReached =
                results.Count >= RecursiveSearchResultLimit;
            SearchQuery = query;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            if (token.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            ErrorMessage = "Windows не разрешила выполнить поиск в этой папке.";
            StatusText = "Нет доступа";
        }
        catch (DirectoryNotFoundException exception)
        {
            if (token.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            ErrorMessage = exception.Message;
            StatusText = "Папка не найдена";
        }
        catch (IOException)
        {
            if (token.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            ErrorMessage = "Поиск прерван: накопитель или папка больше недоступны.";
            StatusText = "Ошибка поиска";
        }
        finally
        {
            if (!token.IsCancellationRequested
                && navigationVersion == Volatile.Read(ref _navigationVersion))
            {
                IsBusy = false;
            }
        }
    }

    public async Task OpenEntryAsync(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateFolderAsync(entry.FullPath);
            return;
        }

        Process.Start(new ProcessStartInfo(entry.FullPath)
        {
            UseShellExecute = true
        });

        AddRecent(entry);
    }

    public void OpenInExplorer(FileSystemEntry? entry = null)
    {
        var targetPath = entry?.FullPath ?? CurrentPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var arguments = entry is { IsDirectory: false }
            ? $"/select,\"{targetPath}\""
            : $"\"{targetPath}\"";

        Process.Start(new ProcessStartInfo("explorer.exe", arguments)
        {
            UseShellExecute = true
        });
    }

    public void LaunchGame(GameEntry game)
    {
        _gameLibrary.Launch(game);
    }

    public void OpenGameFolder(GameEntry game)
    {
        _gameLibrary.OpenInstallFolder(game);
    }

    public async Task<bool> AddLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var added = await _gameLibrary.AddLocalGameFolderAsync(
            path,
            cancellationToken);
        StatusText = added
            ? "Папка локальных игр добавлена"
            : "Эта папка уже проверяется";
        if (IsGames)
        {
            await RefreshAsync();
        }

        return added;
    }

    public IReadOnlyList<string> GetLocalGameFolders() =>
        _gameLibrary.GetLocalGameFolders();

    public async Task<bool> RemoveLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var removed = await _gameLibrary.RemoveLocalGameFolderAsync(
            path,
            cancellationToken);
        StatusText = removed
            ? "Источник игр удалён из списка"
            : "Источник уже отсутствует";
        if (removed && IsGames)
        {
            await RefreshAsync();
        }

        return removed;
    }

    public async Task SetShowHiddenItemsAsync(
        bool value,
        CancellationToken cancellationToken = default)
    {
        await _fileSystem.SetShowHiddenItemsAsync(value, cancellationToken);
        OnPropertyChanged(nameof(ShowHiddenItems));
        StatusText = value
            ? "Скрытые элементы показаны"
            : "Скрытые элементы скрыты";
        if (_currentLocation is
            {
                Kind: NavigationKind.Folder
                    or NavigationKind.Archive
                    or NavigationKind.OptionalFolder
            })
        {
            await RefreshAsync();
        }
    }

    public bool SetShowSystemApplications(bool value)
    {
        if (_showSystemApplications == value)
        {
            return true;
        }

        _showSystemApplications = value;
        OnPropertyChanged(nameof(ShowSystemApplications));
        ApplyFilter();
        var saved = TrySaveShowSystemApplications(value);
        StatusText = saved
            ? value
                ? "Системные приложения показаны"
                : "Системные приложения скрыты"
            : "Настройка применена до закрытия Nexus, но не сохранена";
        return saved;
    }

    public void ReportStatus(string message)
    {
        StatusText = message;
    }

    public bool IsFavorite(string path) => _favorites.Contains(path);

    public int HiddenGamesCount => _gameLibrary.HiddenGamesCount;

    public async Task<bool> HideGameAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        var hidden = await _gameLibrary.HideGameAsync(game, cancellationToken);
        if (hidden)
        {
            _allGames.RemoveAll(item =>
                string.Equals(
                    item.InstallPath,
                    game.InstallPath,
                    StringComparison.OrdinalIgnoreCase));
            ApplyFilter();
            StatusText = "Игра скрыта из библиотеки";
            OnPropertyChanged(nameof(HiddenGamesCount));
        }

        return hidden;
    }

    public async Task<int> RestoreHiddenGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var restored = await _gameLibrary.RestoreHiddenGamesAsync(
            cancellationToken);
        OnPropertyChanged(nameof(HiddenGamesCount));
        if (restored > 0 && IsGames)
        {
            await RefreshAsync();
        }

        StatusText = restored == 0
            ? "Скрытых игр нет"
            : $"Возвращено в библиотеку: {restored}";
        return restored;
    }

    public async Task<bool> ToggleFavoriteAsync(
        FileSystemEntry entry,
        CancellationToken cancellationToken = default)
    {
        var added = !_favorites.Contains(entry.FullPath);
        var changed = added
            ? await _favorites.AddAsync(entry.FullPath, cancellationToken)
            : await _favorites.RemoveAsync(entry.FullPath, cancellationToken);

        if (!changed)
        {
            return added;
        }

        StatusText = added
            ? "Добавлено в избранное"
            : "Удалено из избранного";

        if (IsFavorites)
        {
            await RefreshAsync();
        }

        return added;
    }

    public void SetSort(FileSortField field, bool? descending = null)
    {
        _sortField = field;
        if (descending is not null)
        {
            _sortDescending = descending.Value;
        }

        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SortLabel));
        ApplyFilter();
    }

    public void ToggleSortDirection()
    {
        SetSort(_sortField, !_sortDescending);
    }

    private async Task<bool> NavigateAsync(BrowserLocation location, bool addHistory)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        var navigationVersion = Interlocked.Increment(ref _navigationVersion);
        var previousLocation = _currentLocation;

        ErrorMessage = null;
        IsBusy = true;
        StatusText = "Загрузка…";

        try
        {
            IReadOnlyList<FileSystemEntry> entries;
            IReadOnlyList<GameEntry> games = [];
            if (location.Kind == NavigationKind.Games)
            {
                games = await _gameLibrary.GetInstalledGamesAsync(cancellationToken);
                entries = [];
            }
            else
            {
                entries = location.Kind switch
                {
                    NavigationKind.Home => await LoadDashboardRecentAsync(cancellationToken),
                    NavigationKind.Computer => await _fileSystem.GetDrivesAsync(cancellationToken),
                    NavigationKind.Network =>
                        await _fileSystem.GetNetworkLocationsAsync(
                            cancellationToken),
                    NavigationKind.Recent => await LoadRecentAsync(60, cancellationToken),
                    NavigationKind.Favorites => await _fileSystem.GetPathEntriesAsync(
                        _favorites.GetPaths(),
                        cancellationToken),
                    NavigationKind.Archive when location.Path is not null =>
                        await LoadArchiveAsync(location.Path, cancellationToken),
                    NavigationKind.OptionalFolder when location.Path is not null
                        && Directory.Exists(location.Path) =>
                        await _fileSystem.GetDirectoryEntriesAsync(
                            location.Path,
                            cancellationToken),
                    NavigationKind.Collection
                        when string.Equals(
                            location.Title,
                            "MCP",
                            StringComparison.CurrentCultureIgnoreCase) =>
                        await LoadMcpConfigurationsAsync(cancellationToken),
                    NavigationKind.Collection when location.Path is not null =>
                        await _fileSystem.GetPathEntriesAsync(
                            [location.Path],
                            cancellationToken),
                    NavigationKind.Media => await LoadMediaAsync(cancellationToken),
                    NavigationKind.Torrents =>
                        await _fileSystem.GetTorrentFilesAsync(
                            _gameLibrary.GetScanRoots(),
                            cancellationToken),
                    NavigationKind.Applications =>
                        await _fileSystem.GetInstalledApplicationsAsync(cancellationToken),
                    NavigationKind.AiProjects =>
                        await LoadAiProjectsAsync(cancellationToken),
                    NavigationKind.Folder when location.Path is not null =>
                        await _fileSystem.GetDirectoryEntriesAsync(location.Path, cancellationToken),
                    _ => []
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return false;
            }

            if (addHistory
                && previousLocation is not null
                && previousLocation != location)
            {
                _backStack.Push(previousLocation);
                _forwardStack.Clear();
            }

            _currentLocation = location;
            ConfigureWatcher(location);
            CurrentTitle = location.Title;
            CurrentPath = location.Path ?? string.Empty;
            IsHome = location.Kind == NavigationKind.Home;
            IsGames = location.Kind == NavigationKind.Games;
            IsFavorites = location.Kind == NavigationKind.Favorites;
            IsRecursiveSearchResults = false;
            IsSearchResultLimitReached = false;
            SearchQuery = string.Empty;
            _allItems.Clear();
            _allGames.Clear();

            _allGames.AddRange(games);
            if (location.Kind == NavigationKind.Games)
            {
                OnPropertyChanged(nameof(HiddenGamesCount));
            }

            _allItems.AddRange(entries);
            OnPropertyChanged(nameof(HiddenSystemApplicationsCount));
            ApplyFilter();
            NotifyHistoryChanged();
            OnPropertyChanged(nameof(CurrentKind));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            if (cancellationToken.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return false;
            }

            ErrorMessage = "Windows не разрешила открыть эту папку.";
            StatusText = "Нет доступа";
            return false;
        }
        catch (DirectoryNotFoundException exception)
        {
            if (cancellationToken.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return false;
            }

            ErrorMessage = exception.Message;
            StatusText = "Папка не найдена";
            return false;
        }
        catch (IOException)
        {
            if (cancellationToken.IsCancellationRequested
                || navigationVersion != Volatile.Read(ref _navigationVersion))
            {
                return false;
            }

            ErrorMessage = "Не удалось прочитать содержимое. Накопитель мог быть отключён.";
            StatusText = "Ошибка чтения";
            return false;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested
                && navigationVersion == Volatile.Read(ref _navigationVersion))
            {
                IsBusy = false;
            }
        }
    }

    private async Task<IReadOnlyList<FileSystemEntry>> LoadAiProjectsAsync(
        CancellationToken cancellationToken)
    {
        var projects = await _aiWorkspace.DiscoverProjectsAsync(
            cancellationToken);
        return projects
            .Select(project => new FileSystemEntry(
                project.Name,
                project.FullPath,
                IsDirectory: true,
                project.ModifiedAt,
                SizeBytes: null,
                project.DisplayKind,
                project.Kind switch
                {
                    AiProjectKind.ModelWorkspace => "\uF158",
                    AiProjectKind.AgentWorkspace => "\uE945",
                    AiProjectKind.AiTool => "\uE943",
                    _ => "\uE8B7"
                }))
            .ToArray();
    }

    private async Task<IReadOnlyList<FileSystemEntry>> LoadMcpConfigurationsAsync(
        CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var candidates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(profile, ".codex", "config.toml"),
            Path.Combine(profile, ".cursor", "mcp.json"),
            Path.Combine(roaming, "Claude", "claude_desktop_config.json"),
            Path.Combine(roaming, "Code", "User", "mcp.json")
        };

        var projectRoots = (await _aiWorkspace.DiscoverProjectsAsync(
                cancellationToken))
            .Select(project => project.FullPath);
        foreach (var root in _aiWorkspace.GetSearchRoots()
                     .Concat(projectRoots)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(Path.Combine(root, ".mcp.json"));
            candidates.Add(Path.Combine(root, "mcp.json"));
            candidates.Add(Path.Combine(root, ".vscode", "mcp.json"));
            candidates.Add(Path.Combine(root, ".cursor", "mcp.json"));
        }

        return await _fileSystem.GetPathEntriesAsync(
            candidates.Where(File.Exists),
            cancellationToken);
    }

    private static BrowserLocationState ToState(BrowserLocation location) =>
        new(location.Kind, location.Title, location.Path);

    private static BrowserLocation FromState(BrowserLocationState state) =>
        new(state.Kind, state.Title, state.Path);

    private void ConfigureWatcher(BrowserLocation location)
    {
        StopWatcher();

        if (location.Kind is not (
                NavigationKind.Folder
                or NavigationKind.Archive
                or NavigationKind.OptionalFolder)
            || string.IsNullOrWhiteSpace(location.Path)
            || !Directory.Exists(location.Path))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(location.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
            };
            watcher.Created += OnWatchedFolderChanged;
            watcher.Deleted += OnWatchedFolderChanged;
            watcher.Changed += OnWatchedFolderChanged;
            watcher.Renamed += OnWatchedFolderChanged;
            lock (_watchGate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource cancellation;
        lock (_watchGate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
            {
                return;
            }

            previous = _watchDebounceCancellation;
            cancellation = new CancellationTokenSource();
            _watchDebounceCancellation = cancellation;
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            previous?.Dispose();
        }

        _ = NotifyFolderChangedAsync(cancellation.Token);
    }

    private async Task NotifyFolderChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            ActiveFolderChangedExternally?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        lock (_watchGate)
        {
            _disposed = true;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        StopWatcher();
        GC.SuppressFinalize(this);
    }

    private void StopWatcher()
    {
        FileSystemWatcher? watcher;
        CancellationTokenSource? debounce;
        lock (_watchGate)
        {
            watcher = _watcher;
            _watcher = null;
            debounce = _watchDebounceCancellation;
            _watchDebounceCancellation = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnWatchedFolderChanged;
            watcher.Deleted -= OnWatchedFolderChanged;
            watcher.Changed -= OnWatchedFolderChanged;
            watcher.Renamed -= OnWatchedFolderChanged;
            watcher.Dispose();
        }

        try
        {
            debounce?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            debounce?.Dispose();
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<FileSystemEntry> visibleItems = _allItems;
        if (CurrentKind == NavigationKind.Applications
            && !_showSystemApplications)
        {
            visibleItems = visibleItems.Where(item => !IsSystemApplication(item));
        }

        var filtered = string.IsNullOrWhiteSpace(SearchQuery)
            ? visibleItems
            : visibleItems
                .Where(item =>
                    item.Name.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || item.FullPath.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || item.DisplayType.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        var sorted = SortItems(filtered);
        ReplaceAll(Items, sorted);

        var filteredGames = string.IsNullOrWhiteSpace(SearchQuery)
            ? _allGames
            : _allGames
                .Where(game =>
                    game.Name.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || game.SourceLabel.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || game.InstallPath.Contains(
                        SearchQuery,
                        StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        ReplaceAll(Games, filteredGames);

        if (IsHome)
        {
            ReplaceAll(RecentItems, sorted);
        }

        StatusText = IsRecursiveSearchResults
            && IsSearchResultLimitReached
                ? $"Показаны первые {RecursiveSearchResultLimit:N0} результатов"
            : IsHome
            ? CurrentTitle
            : IsGames
                ? Games.Count switch
                {
                    0 => "Игры не найдены",
                    1 => "1 игра",
                    >= 2 and <= 4 => $"{Games.Count} игры",
                    _ => $"{Games.Count} игр"
                }
            : Items.Count switch
            {
                0 => "Нет элементов",
                1 => "1 элемент",
                >= 2 and <= 4 => $"{Items.Count} элемента",
                _ => $"{Items.Count} элементов"
        };
    }

    private static bool IsSystemApplication(FileSystemEntry entry) =>
        string.Equals(
            entry.DisplayType,
            "Системное приложение",
            StringComparison.CurrentCultureIgnoreCase);

    private static bool LoadShowSystemApplications()
    {
        try
        {
            var path = File.Exists(InterfacePreferencesPath)
                ? InterfacePreferencesPath
                : LegacyInterfacePreferencesPath;
            return File.Exists(path)
                && string.Equals(
                    File.ReadAllText(path).Trim(),
                    "1",
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TrySaveShowSystemApplications(bool value)
    {
        try
        {
            var directory = Path.GetDirectoryName(InterfacePreferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                InterfacePreferencesPath,
                value ? "1" : "0");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReplaceAll<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> items)
    {
        if (collection is BulkObservableCollection<T> bulkCollection)
        {
            bulkCollection.ReplaceAll(items);
            return;
        }

        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private void AddRecent(FileSystemEntry entry)
    {
        var existing = RecentItems.FirstOrDefault(item =>
            string.Equals(item.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            RecentItems.Remove(existing);
        }

        RecentItems.Insert(0, entry);

        while (RecentItems.Count > 6)
        {
            RecentItems.RemoveAt(RecentItems.Count - 1);
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanNavigateUp));
    }

    private Task<IReadOnlyList<FileSystemEntry>> LoadDashboardRecentAsync(
        CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return _fileSystem.GetRecentFilesAsync(
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        ],
        6,
        cancellationToken);
    }

    private Task<IReadOnlyList<FileSystemEntry>> LoadRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return _fileSystem.GetRecentFilesAsync(
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "OneDrive", "Documents"),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "OneDrive", "Рабочий стол")
        ],
        limit,
        cancellationToken);
    }

    private async Task<IReadOnlyList<FileSystemEntry>> LoadMediaAsync(
        CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entries = await _fileSystem.GetRecentFilesAsync(
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(profile, "OneDrive", "Pictures")
        ],
        500,
        cancellationToken);

        return entries
            .Where(entry => IsMediaFile(entry.FullPath))
            .Take(120)
            .ToArray();
    }

    private static bool IsMediaFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg"
            or ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm"
            or ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac";
    }

    private async Task<IReadOnlyList<FileSystemEntry>> LoadArchiveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await Task.Run(
            () => Directory.CreateDirectory(path),
            cancellationToken);
        return await _fileSystem.GetDirectoryEntriesAsync(path, cancellationToken);
    }

    private IReadOnlyList<FileSystemEntry> SortItems(
        IEnumerable<FileSystemEntry> items)
    {
        var folders = items.Where(item => item.IsDirectory);
        var files = items.Where(item => !item.IsDirectory);
        return ApplySort(folders)
            .Concat(ApplySort(files))
            .ToArray();
    }

    private IOrderedEnumerable<FileSystemEntry> ApplySort(
        IEnumerable<FileSystemEntry> items)
    {
        Func<FileSystemEntry, object?> selector = _sortField switch
        {
            FileSortField.Modified => item => item.ModifiedAt,
            FileSortField.Type => item => item.DisplayType,
            FileSortField.Size => item => item.SizeBytes ?? -1,
            _ => item => item.Name
        };

        var comparer = Comparer<object?>.Create(CompareSortValues);
        return _sortDescending
            ? items
                .OrderByDescending(selector, comparer)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            : items
                .OrderBy(selector, comparer)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int CompareSortValues(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left is string leftText && right is string rightText)
        {
            return StringComparer.CurrentCultureIgnoreCase.Compare(
                leftText,
                rightText);
        }

        return left is IComparable comparable
            ? comparable.CompareTo(right)
            : StringComparer.CurrentCultureIgnoreCase.Compare(
                left.ToString(),
                right.ToString());
    }

    private sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            var replacement = items as IReadOnlyCollection<T>
                ?? items.ToArray();

            Items.Clear();
            foreach (var item in replacement)
            {
                Items.Add(item);
            }

            OnPropertyChanged(
                new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(
                new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Reset));
        }
    }

    private sealed record BrowserLocation(
        NavigationKind Kind,
        string Title,
        string? Path);
}

public sealed record BrowserLocationState(
    NavigationKind Kind,
    string Title,
    string? Path);

public sealed record BrowserSessionState(
    BrowserLocationState Current,
    IReadOnlyList<BrowserLocationState> Back,
    IReadOnlyList<BrowserLocationState> Forward,
    FileSortField SortField,
    bool SortDescending,
    string SearchQuery,
    bool IsRecursiveSearchResults);

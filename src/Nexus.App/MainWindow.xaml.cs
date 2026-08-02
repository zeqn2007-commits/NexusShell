using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Services;
using Nexus.App.ViewModels;
using Nexus.Core.Models;
using Nexus.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Nexus.App;

public sealed partial class MainWindow : Window
{
    private readonly IOrganizationService _organizationService = new OrganizationService();
    private readonly IFileOperationService _fileOperationService = new FileOperationService();
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();
    private readonly IAiWorkspaceService _aiWorkspaceService =
        new AiWorkspaceService();
    private readonly ShellThumbnailService _thumbnailService = new();
    private readonly ConditionalWeakTable<Image, ThumbnailImageState>
        _thumbnailImageStates = new();
    private readonly IReadOnlyList<Button> _navigationButtons;
    private readonly IReadOnlyList<TextBlock> _navigationLabels;
    private IReadOnlyList<string> _clipboardPaths = [];
    private ClipboardOperation _clipboardOperation;
    private FileSystemEntry? _selectedEntry;
    private GameEntry? _selectedGame;
    private RecycleBinListItem? _selectedRecycleBinItem;
    private OrganizationSuggestion? _currentSuggestion;
    private OrganizationOperation? _lastOrganizationOperation;
    private string? _operationErrorMessage;
    private bool _isGridMode;
    private bool _dashboardGridMode = true;
    private bool _isHomeTabActive;
    private bool _isWorkspaceTabActive;
    private bool _interfaceUpdatePending;
    private BrowserSessionState? _homeSession;
    private BrowserSessionState? _dashboardSession;
    private readonly List<WorkspaceTabState> _workspaceTabs = [];
    private WorkspaceTabState? _activeWorkspaceTab;
    private string? _workspaceTabVisualSignature;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchDebounceTimer;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _aiCenterLoadCancellation;
    private CancellationTokenSource? _recycleBinLoadCancellation;
    private CancellationTokenSource? _recycleBinOperationCancellation;
    private FileOperationPauseController? _operationPauseController;
    private bool _fileOperationRunning;
    private bool _fileOperationPaused;
    private bool _suppressSearchTextChanged;
    private long _searchGeneration;
    private bool _isRecycleBinActive;
    private bool _isRecycleBinLoading;
    private bool _isRecycleBinOperationRunning;
    private bool _isSettingsActive;
    private bool _recycleBinGridMode;
    private string? _recycleBinErrorMessage;
    private IReadOnlyList<RecycleBinListItem> _allRecycleBinItems = [];
    private bool _externalRefreshPending;
    private bool _rootUnloaded;
    private bool _aiCenterMetadataLoaded;
    private DateTimeOffset _aiCenterMetadataLoadedAt;

    public MainWindowViewModel ViewModel { get; }

    public ObservableCollection<RecycleBinListItem> RecycleBinItems { get; } = [];

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel(
            new FileSystemService(),
            new GameLibraryService(),
            new FavoritesService(),
            _aiWorkspaceService);
        InitializeComponent();

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += async (_, _) =>
        {
            var generation = Volatile.Read(ref _searchGeneration);
            var query = SearchBox.Text.Trim();
            if (_isSettingsActive)
            {
                return;
            }

            if (_isRecycleBinActive)
            {
                ApplyRecycleBinFilter(query);
                return;
            }

            if (ViewModel.IsRecursiveSearchResults)
            {
                await ViewModel.RefreshAsync();
                if (generation != Volatile.Read(ref _searchGeneration)
                    || _isSettingsActive
                    || _isRecycleBinActive
                    || !string.Equals(
                        SearchBox.Text.Trim(),
                        query,
                        StringComparison.Ordinal))
                {
                    return;
                }

                CaptureActiveSession();
            }

            if (generation != Volatile.Read(ref _searchGeneration))
            {
                return;
            }

            ViewModel.SearchQuery = query;
            CaptureActiveSession();
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var horizontalMargin = workArea.Width >= 2560 ? 96 : 48;
        var verticalMargin = workArea.Height >= 1200 ? 72 : 48;
        var maximumWidth = workArea.Width >= 2560 ? 2400 : 1500;
        var maximumHeight = workArea.Height >= 1200 ? 1200 : 860;
        var windowWidth = Math.Max(
            720,
            Math.Min(maximumWidth, workArea.Width - horizontalMargin));
        var windowHeight = Math.Max(
            560,
            Math.Min(maximumHeight, workArea.Height - verticalMargin));
        var windowX = workArea.X + Math.Max(0, (workArea.Width - windowWidth) / 2);
        var windowY = workArea.Y + Math.Max(0, (workArea.Height - windowHeight) / 2);
        AppWindow.MoveAndResize(new RectInt32(windowX, windowY, windowWidth, windowHeight));

        if (AppWindow.TitleBar is not null)
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(150, 255, 255, 255);
        }

        _navigationButtons =
        [
            HomeNavigationButton,
            RecentNavigationButton,
            FavoritesNavigationButton,
            InboxNavigationButton,
            WorkNavigationButton,
            ProjectsNavigationButton,
            PersonalNavigationButton,
            MediaNavigationButton,
            GamesNavigationButton,
            ApplicationsNavigationButton,
            TorrentsNavigationButton,
            ArchiveNavigationButton,
            ComputerNavigationButton,
            NetworkNavigationButton,
            DocumentsNavigationButton,
            DownloadsNavigationButton,
            DesktopNavigationButton,
            PicturesNavigationButton,
            VideosNavigationButton,
            MusicNavigationButton,
            RecycleNavigationButton,
            AiCenterNavigationButton,
            ModelsNavigationButton,
            AiProjectsNavigationButton,
            SkillsNavigationButton,
            McpNavigationButton,
            PromptsNavigationButton,
            SettingsNavigationButton
        ];

        _navigationLabels =
        [
            HomeNavigationLabel,
            RecentNavigationLabel,
            FavoritesNavigationLabel,
            InboxNavigationLabel,
            WorkNavigationLabel,
            ProjectsNavigationLabel,
            PersonalNavigationLabel,
            MediaNavigationLabel,
            GamesNavigationLabel,
            ApplicationsNavigationLabel,
            TorrentsNavigationLabel,
            ArchiveNavigationLabel,
            ComputerNavigationLabel,
            NetworkNavigationLabel,
            DocumentsNavigationLabel,
            DownloadsNavigationLabel,
            DesktopNavigationLabel,
            PicturesNavigationLabel,
            VideosNavigationLabel,
            MusicNavigationLabel,
            RecycleNavigationLabel,
            AiCenterNavigationLabel,
            ModelsNavigationLabel,
            AiProjectsNavigationLabel,
            SkillsNavigationLabel,
            McpNavigationLabel,
            PromptsNavigationLabel,
            SettingsNavigationLabel,
            ExplorerFallbackLabel
        ];

        foreach (var button in _navigationButtons)
        {
            if (button.Tag is not string id)
            {
                continue;
            }

            var label = ViewModel.NavigationTargets
                .FirstOrDefault(target => target.Id == id)
                ?.Label;
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
        }

        ConfigureNamedButton(
            RecycleNavigationButton,
            "Корзина");
        ConfigureNamedButton(
            SettingsNavigationButton,
            "Настройки");
        ConfigureNamedButton(
            ExplorerFallbackButton,
            "Открыть стандартный Проводник");

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ActiveFolderChangedExternally +=
            ViewModel_ActiveFolderChangedExternally;
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ViewModel.RecentItems.CollectionChanged += RecentItems_CollectionChanged;
        ViewModel.Games.CollectionChanged += Games_CollectionChanged;
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.Unloaded += RootGrid_Unloaded;

        SetNavigationSelection(ProjectsNavigationButton);
        SetViewMode(gridMode: false);
        SetDashboardViewMode(gridMode: true);
        SetTabVisuals();
    }

    private static void ConfigureNamedButton(Button button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _rootUnloaded = false;
        await ViewModel.InitializeAsync();
        _dashboardSession = ViewModel.CaptureSession();
        await LoadDashboardMetadataAsync();
        ClosePreview();
        UpdateInterfaceState();
    }

    private void RootGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        _rootUnloaded = true;
        _searchDebounceTimer.Stop();
        _aiCenterLoadCancellation?.Cancel();
        _aiCenterLoadCancellation?.Dispose();
        _aiCenterLoadCancellation = null;
        _recycleBinLoadCancellation?.Cancel();
        _recycleBinLoadCancellation?.Dispose();
        _recycleBinLoadCancellation = null;
        _recycleBinOperationCancellation?.Cancel();
        _recycleBinOperationCancellation?.Dispose();
        _recycleBinOperationCancellation = null;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.ActiveFolderChangedExternally -=
            ViewModel_ActiveFolderChangedExternally;
        ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
        ViewModel.RecentItems.CollectionChanged -= RecentItems_CollectionChanged;
        ViewModel.Games.CollectionChanged -= Games_CollectionChanged;
        ViewModel.Dispose();
    }

    private void ViewModel_ActiveFolderChangedExternally(
        object? sender,
        EventArgs e)
    {
        if (_externalRefreshPending)
        {
            return;
        }

        _externalRefreshPending = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    while (!_rootUnloaded
                           && (_fileOperationRunning || ViewModel.IsBusy))
                    {
                        await Task.Delay(250);
                    }

                    if (!_rootUnloaded)
                    {
                        await ViewModel.RefreshAsync();
                        CaptureActiveSession();
                    }
                }
                finally
                {
                    _externalRefreshPending = false;
                }
            }))
        {
            _externalRefreshPending = false;
        }
    }

    private async void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        CaptureActiveSession();
        if (id == "home")
        {
            _isHomeTabActive = true;
            _isWorkspaceTabActive = false;
        }
        else if (id is "projects" or "ai-center")
        {
            _isHomeTabActive = false;
            _isWorkspaceTabActive = false;
        }
        else
        {
            _isHomeTabActive = false;
            _isWorkspaceTabActive = true;
            WorkspaceTabsScroller.Visibility = Visibility.Visible;
        }

        ClearOperationError();
        SetNavigationSelection(button);
        ClosePreview();

        await ViewModel.NavigateToTargetAsync(id);
        CaptureActiveSession();

        if (ViewModel.IsHome)
        {
            if (id == "ai-center"
                && (!_aiCenterMetadataLoaded
                    || DateTimeOffset.Now - _aiCenterMetadataLoadedAt
                    > TimeSpan.FromMinutes(2)))
            {
                await LoadAiCenterMetadataAsync(force: false);
            }
        }
    }

    private async void SpaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ClearOperationError();
            CaptureActiveSession();
            _isHomeTabActive = false;
            _isWorkspaceTabActive = true;
            WorkspaceTabsScroller.Visibility = Visibility.Visible;
            SetNavigationSelectionById(id);
            ClosePreview();
            await ViewModel.NavigateToTargetAsync(id);
            CaptureActiveSession();
        }
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ClearOperationError();
        ClosePreview();
        await ViewModel.GoBackAsync();
        CaptureActiveSession();
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        ClearOperationError();
        ClosePreview();
        await ViewModel.GoForwardAsync();
        CaptureActiveSession();
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        ClearOperationError();
        ClosePreview();
        ClearNavigationSelection();
        await ViewModel.GoUpAsync();
        CaptureActiveSession();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ClearOperationError();
        ClosePreview();
        if (_isRecycleBinActive)
        {
            await LoadRecycleBinAsync();
            return;
        }

        await ViewModel.RefreshAsync();
        if (ViewModel.IsHome)
        {
            await LoadDashboardMetadataAsync(forceAiRefresh: true);
        }
        CaptureActiveSession();
    }

    private async void AiCenterRefreshButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadAiCenterMetadataAsync(force: true);
    }

    private async void BrowserEmptyActionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.SearchQuery))
        {
            SetSearchBoxText(string.Empty);
            _searchDebounceTimer.Stop();
            await ViewModel.RefreshAsync();
            CaptureActiveSession();
            return;
        }

        var targetId = ViewModel.CurrentKind switch
        {
            NavigationKind.Favorites => "projects",
            NavigationKind.Recent => "documents",
            NavigationKind.Torrents => "downloads",
            _ => null
        };
        if (targetId is not null)
        {
            _isHomeTabActive = false;
            _isWorkspaceTabActive = true;
            WorkspaceTabsScroller.Visibility = Visibility.Visible;
            SetNavigationSelectionById(targetId);
            await ViewModel.NavigateToTargetAsync(targetId);
            CaptureActiveSession();
            return;
        }

        if (ViewModel.CurrentKind == NavigationKind.Applications)
        {
            ManageApplications_Click(sender, e);
            return;
        }

        if (ViewModel.CurrentKind == NavigationKind.Collection
            && string.Equals(
                ViewModel.CurrentTitle,
                "MCP",
                StringComparison.CurrentCultureIgnoreCase))
        {
            await OpenMcpConfigurationFolderAsync();
            return;
        }

        if (ViewModel.CurrentKind == NavigationKind.OptionalFolder)
        {
            await EnsureOptionalLocationAsync();
            return;
        }

        if (CanModifyCurrentFolder())
        {
            NewFolderCommandButton_Click(sender, e);
            return;
        }

        await ViewModel.RefreshAsync();
        CaptureActiveSession();
    }

    private async Task EnsureOptionalLocationAsync()
    {
        if (ViewModel.CurrentKind != NavigationKind.OptionalFolder
            || string.IsNullOrWhiteSpace(ViewModel.CurrentPath))
        {
            return;
        }

        if (Directory.Exists(ViewModel.CurrentPath))
        {
            await CreateFolderAsync();
            return;
        }

        try
        {
            Directory.CreateDirectory(ViewModel.CurrentPath);
            ViewModel.ReportStatus($"Создано расположение: {ViewModel.CurrentPath}");
            await ViewModel.RefreshAsync();
            CaptureActiveSession();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            ShowOperationError(
                $"Не удалось создать расположение: {exception.Message}");
        }
    }

    private async Task OpenMcpConfigurationFolderAsync()
    {
        var configPath = ViewModel.CurrentPath;
        var directory = string.IsNullOrWhiteSpace(configPath)
            ? null
            : Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            ShowOperationError("Папка конфигурации MCP не определена.");
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            ClearNavigationSelection();
            await ViewModel.NavigateFolderAsync(directory, ".codex");
            CaptureActiveSession();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            ShowOperationError(
                $"Не удалось открыть папку конфигурации MCP: {exception.Message}");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextChanged)
        {
            return;
        }

        Interlocked.Increment(ref _searchGeneration);
        _searchDebounceTimer.Stop();
        if (!_isSettingsActive)
        {
            _searchDebounceTimer.Start();
        }
    }

    private void SetSearchBoxText(string value)
    {
        _searchDebounceTimer.Stop();
        Interlocked.Increment(ref _searchGeneration);
        _suppressSearchTextChanged = true;
        try
        {
            SearchBox.Text = value;
        }
        finally
        {
            _suppressSearchTextChanged = false;
        }
    }

    private async void SearchBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (_isSettingsActive)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SetSearchBoxText(string.Empty);
            if (_isRecycleBinActive)
            {
                ApplyRecycleBinFilter(string.Empty);
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (ViewModel.IsBusy
                || ViewModel.IsRecursiveSearchResults
                || !string.IsNullOrWhiteSpace(ViewModel.SearchQuery))
            {
                await ViewModel.RefreshAsync();
                CaptureActiveSession();
            }

            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        var generation = Interlocked.Increment(ref _searchGeneration);
        e.Handled = true;
        ClosePreview();
        if (_isRecycleBinActive)
        {
            ApplyRecycleBinFilter(SearchBox.Text);
            return;
        }

        var query = SearchBox.Text;
        await ViewModel.SearchCurrentFolderAsync(query);
        if (generation == Volatile.Read(ref _searchGeneration)
            && string.Equals(SearchBox.Text, query, StringComparison.Ordinal))
        {
            CaptureActiveSession();
        }
    }

    private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isRecycleBinActive || _isSettingsActive)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            AddressBox.Text = ViewModel.DisplayAddress;
            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await NavigateFromAddressAsync();
    }

    private async Task NavigateFromAddressAsync()
    {
        var rawAddress = AddressBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            ShowOperationError("Введите путь к папке.");
            return;
        }

        try
        {
            var path = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(rawAddress));
            if (Directory.Exists(path))
            {
                _isHomeTabActive = false;
                _isWorkspaceTabActive = true;
                WorkspaceTabsScroller.Visibility = Visibility.Visible;
                ClearNavigationSelection();
                ClosePreview();
                SetViewMode(gridMode: false);
                await ViewModel.NavigateFolderAsync(path);
                RootGrid.Focus(FocusState.Programmatic);
                return;
            }

            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
                ViewModel.ReportStatus($"Открыт файл: {Path.GetFileName(path)}");
                RootGrid.Focus(FocusState.Programmatic);
                return;
            }

            ShowOperationError("Такой папки или файла не существует.");
            AddressBox.SelectAll();
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.ComponentModel.Win32Exception)
        {
            ShowOperationError("Не удалось открыть указанный путь.");
            AddressBox.SelectAll();
        }
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        AddSortItem(flyout, "По имени", FileSortField.Name);
        AddSortItem(flyout, "По дате изменения", FileSortField.Modified);
        AddSortItem(flyout, "По типу", FileSortField.Type);
        AddSortItem(flyout, "По размеру", FileSortField.Size);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var directionItem = new ToggleMenuFlyoutItem
        {
            Text = "Обратный порядок",
            IsChecked = ViewModel.SortDescending
        };
        directionItem.Click += (_, _) => ViewModel.ToggleSortDirection();
        flyout.Items.Add(directionItem);
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void DashboardMoreButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        AddContextCommand(
            flyout,
            "Новая рабочая вкладка",
            NewTabButton_Click,
            enabled: true);
        AddContextCommand(
            flyout,
            "Обновить",
            RefreshButton_Click,
            enabled: !ViewModel.IsBusy);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddContextCommand(
            flyout,
            "Настройки Nexus",
            SettingsNavigationButton_Click,
            enabled: true);
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void AddSortItem(
        MenuFlyout flyout,
        string text,
        FileSortField field)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = ViewModel.SortField == field
        };
        item.Click += (_, _) => ViewModel.SetSort(field);
        flyout.Items.Add(item);
    }

    private void ColumnSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fieldName }
            || !Enum.TryParse<FileSortField>(
                fieldName,
                ignoreCase: true,
                out var field))
        {
            return;
        }

        if (ViewModel.SortField == field)
        {
            ViewModel.ToggleSortDirection();
        }
        else
        {
            ViewModel.SetSort(field, descending: false);
        }
    }

    private void UpdateSortHeaderIndicators()
    {
        NameSortHeaderButton.Content = GetSortHeaderText(
            "Имя",
            FileSortField.Name);
        ModifiedSortHeaderButton.Content = GetSortHeaderText(
            "Изменён",
            FileSortField.Modified);
        TypeSortHeaderButton.Content = GetSortHeaderText(
            "Тип",
            FileSortField.Type);
        SizeSortHeaderButton.Content = GetSortHeaderText(
            "Размер",
            FileSortField.Size);
    }

    private string GetSortHeaderText(string label, FileSortField field)
    {
        if (ViewModel.SortField != field)
        {
            return label;
        }

        return $"{label} {(ViewModel.SortDescending ? "↓" : "↑")}";
    }

    private void ListModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(gridMode: false);
    }

    private void DashboardListModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDashboardViewMode(gridMode: false);
    }

    private void GridModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(gridMode: true);
    }

    private void DashboardGridModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDashboardViewMode(gridMode: true);
    }

    private void ToggleViewModeCommandButton_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(!_isGridMode);
    }

    private async void BrowserItems_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var entry = sender switch
        {
            ListView listView => listView.SelectedItem as FileSystemEntry,
            GridView gridView => gridView.SelectedItem as FileSystemEntry,
            _ => null
        };

        if (entry is null)
        {
            return;
        }

        ClosePreview();

        try
        {
            await ViewModel.OpenEntryAsync(entry);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or IOException)
        {
            ShowOperationError("Не удалось открыть выбранный элемент.");
        }
    }

    private void BrowserItems_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase view)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var container = source is null
            ? null
            : FindVisualAncestor<SelectorItem>(source);
        if (container is not null
            && view.ItemFromContainer(container) is FileSystemEntry entry)
        {
            if (!view.SelectedItems.Contains(entry))
            {
                view.SelectedItems.Clear();
                view.SelectedItems.Add(entry);
            }
        }
        else
        {
            view.SelectedItems.Clear();
        }

        ShowBrowserContextMenu(view, e.GetPosition(view));
        e.Handled = true;
    }

    private void ShowBrowserContextMenu(
        ListViewBase view,
        Windows.Foundation.Point? position = null)
    {
        var selected = GetSelectedEntries();
        var flyout = new MenuFlyout();
        if (selected.Count > 0)
        {
            AddContextCommand(
                flyout,
                "Открыть",
                OpenSelectionCommand_Click,
                selected.Count == 1
                && GetEntryCapabilities(selected[0]).CanOpen);
            AddContextCommand(
                flyout,
                "Открыть в новой вкладке",
                OpenSelectionInNewTab_Click,
                selected.Count == 1
                && selected[0].IsDirectory
                && Directory.Exists(selected[0].FullPath));
            AddContextCommand(
                flyout,
                "Открыть с помощью…",
                OpenSelectionWith_Click,
                selected.Count == 1
                && !selected[0].IsDirectory
                && File.Exists(selected[0].FullPath));
            AddContextCommand(
                flyout,
                "Показать в стандартном Проводнике",
                ShowSelectionInExplorer_Click,
                selected.Count == 1
                && !IsVirtualApplicationEntry(selected[0]));
            if (ViewModel.CurrentKind == NavigationKind.Applications)
            {
                AddContextCommand(
                    flyout,
                    "Управление приложениями Windows",
                    ManageApplications_Click,
                    enabled: true);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextCommand(
                flyout,
                "Вырезать",
                CutCommandButton_Click,
                selected.All(item => GetEntryCapabilities(item).CanCut));
            AddContextCommand(
                flyout,
                "Копировать",
                CopyCommandButton_Click,
                selected.All(item => GetEntryCapabilities(item).CanCopy));
            AddContextCommand(
                flyout,
                "Переименовать",
                RenameCommandButton_Click,
                selected.Count == 1
                && GetEntryCapabilities(selected[0]).CanRename);
            AddContextCommand(
                flyout,
                selected.Count == 1
                && ViewModel.IsFavorite(selected[0].FullPath)
                    ? "Удалить из избранного"
                    : "Добавить в избранное",
                FavoriteCommandButton_Click,
                selected.Count == 1
                && GetEntryCapabilities(selected[0]).CanFavorite);
            AddContextCommand(
                flyout,
                "Копировать путь",
                CopyPathButton_Click,
                enabled: true);
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextCommand(
                flyout,
                "Удалить в Корзину",
                DeleteCommandButton_Click,
                selected.All(item => GetEntryCapabilities(item).CanDelete),
                "\uE74D",
                "Delete");
            AddContextCommand(
                flyout,
                "Свойства",
                PropertiesCommandButton_Click,
                selected.All(item =>
                    GetEntryCapabilities(item).CanShowProperties));
        }
        else
        {
            AddContextCommand(
                flyout,
                "Новая папка",
                NewFolderCommandButton_Click,
                CanModifyCurrentFolder());
            AddContextCommand(
                flyout,
                "Вставить",
                PasteCommandButton_Click,
                CanModifyCurrentFolder()
                && (_clipboardPaths.Count > 0 || HasSystemFileClipboard()));
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextCommand(
                flyout,
                "Обновить",
                RefreshButton_Click,
                enabled: true);
            AddContextCommand(
                flyout,
                "Открыть терминал здесь",
                OpenTerminalHere_Click,
                Directory.Exists(ViewModel.CurrentPath));
            AddContextCommand(
                flyout,
                "Открыть в стандартном Проводнике",
                ExplorerFallbackButton_Click,
                enabled: true);
        }

        var options = new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        if (position is { } requestedPosition)
        {
            options.Position = requestedPosition;
        }

        flyout.ShowAt(view, options);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void AddContextCommand(
        MenuFlyout flyout,
        string text,
        RoutedEventHandler handler,
        bool enabled,
        string? glyph = null,
        string? shortcut = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = enabled,
            KeyboardAcceleratorTextOverride = shortcut ?? string.Empty
        };
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        item.Click += handler;
        flyout.Items.Add(item);
    }

    private async void OpenSelectionCommand_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedEntryAsync();
    }

    private async Task OpenSelectedEntryAsync()
    {
        var entries = GetSelectedEntries();
        if (entries.Count != 1)
        {
            return;
        }

        ClosePreview();
        try
        {
            await ViewModel.OpenEntryAsync(entries[0]);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or IOException)
        {
            ShowOperationError("Не удалось открыть выбранный элемент.");
        }
    }

    private async void OpenSelectionInNewTab_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        var entry = selected.Count == 1 ? selected[0] : null;
        if (entry is not { IsDirectory: true }
            || !Directory.Exists(entry.FullPath))
        {
            return;
        }

        CaptureActiveSession();
        var session = new BrowserSessionState(
            new BrowserLocationState(
                NavigationKind.Folder,
                entry.Name,
                entry.FullPath),
            [],
            [],
            ViewModel.SortField,
            ViewModel.SortDescending,
            string.Empty,
            false);
        var tab = new WorkspaceTabState(session)
        {
            GridMode = _isGridMode
        };
        _workspaceTabs.Add(tab);
        await ActivateWorkspaceTabAsync(tab);
    }

    private void OpenSelectionWith_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        var entry = selected.Count == 1 ? selected[0] : null;
        if (entry is null
            || entry.IsDirectory
            || !File.Exists(entry.FullPath))
        {
            return;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                "rundll32.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            startInfo.ArgumentList.Add(entry.FullPath);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError("Не удалось открыть системный выбор приложения.");
        }
    }

    private void ShowSelectionInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        var entry = selected.Count == 1 ? selected[0] : null;
        if (entry is null)
        {
            return;
        }

        try
        {
            ViewModel.OpenInExplorer(entry);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError(
                "Не удалось показать выбранный элемент в стандартном Проводнике.");
        }
    }

    private void ManageApplications_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:appsfeatures")
                {
                    UseShellExecute = true
                });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError(
                "Не удалось открыть управление приложениями Windows.");
        }
    }

    private void OpenTerminalHere_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ViewModel.CurrentPath))
        {
            return;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("wt.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(ViewModel.CurrentPath);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError(
                "Windows Terminal не найден. Установите его или откройте терминал вручную.");
        }
    }

    private void BrowserItems_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        var entries = e.Items
            .OfType<FileSystemEntry>()
            .Where(entry => GetEntryCapabilities(entry).CanCopy)
            .ToArray();
        if (entries.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetDataProvider(
            StandardDataFormats.StorageItems,
            async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var storageItems = new List<IStorageItem>();
                    foreach (var entry in entries)
                    {
                        storageItems.Add(entry.IsDirectory
                            ? await StorageFolder.GetFolderFromPathAsync(
                                entry.FullPath)
                            : await StorageFile.GetFileFromPathAsync(
                                entry.FullPath));
                    }

                    request.SetData(storageItems);
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException
                    or FileNotFoundException
                    or System.Runtime.InteropServices.COMException)
                {
                    request.SetData(Array.Empty<IStorageItem>());
                }
                finally
                {
                    deferral.Complete();
                }
            });
        e.Data.RequestedOperation =
            DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private void BrowserView_DragOver(object sender, DragEventArgs e)
    {
        if (!CanModifyCurrentFolder()
            || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var moveRequested = e.Modifiers.HasFlag(
            Microsoft.UI.Input.DragDrop.DragDropModifiers.Shift);
        var copyRequested = e.Modifiers.HasFlag(
            Microsoft.UI.Input.DragDrop.DragDropModifiers.Control);
        e.AcceptedOperation = moveRequested || !copyRequested
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;
        var targetDirectory = GetDropTargetDirectory(e);
        var targetLabel = string.Equals(
                targetDirectory,
                ViewModel.CurrentPath,
                StringComparison.OrdinalIgnoreCase)
            ? ViewModel.CurrentTitle
            : Path.GetFileName(targetDirectory);
        e.DragUIOverride.Caption = moveRequested
            ? $"Переместить в «{targetLabel}»"
            : copyRequested
                ? $"Копировать в «{targetLabel}»"
                : $"Переместить сюда (Ctrl — копировать)";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void BrowserView_Drop(object sender, DragEventArgs e)
    {
        if (!CanModifyCurrentFolder()
            || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            var forceMove = e.Modifiers.HasFlag(
                Microsoft.UI.Input.DragDrop.DragDropModifiers.Shift);
            var forceCopy = e.Modifiers.HasFlag(
                Microsoft.UI.Input.DragDrop.DragDropModifiers.Control);
            var targetDirectory = GetDropTargetDirectory(e);
            var storageItems = await e.DataView.GetStorageItemsAsync();
            var paths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            var targetRoot = Path.GetPathRoot(targetDirectory);
            var moveRequested = forceMove
                || !forceCopy
                && paths.All(path => string.Equals(
                    Path.GetPathRoot(path),
                    targetRoot,
                    StringComparison.OrdinalIgnoreCase));
            await RunTransferOperationAsync((options, progress, token) =>
                moveRequested
                    ? _fileOperationService.MoveAsync(
                        paths,
                        targetDirectory,
                        options,
                        progress,
                        token)
                    : _fileOperationService.CopyAsync(
                        paths,
                        targetDirectory,
                        options,
                        progress,
                        token));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or System.Runtime.InteropServices.COMException)
        {
            ShowOperationError("Не удалось обработать перетаскиваемые элементы.");
        }
    }

    private string GetDropTargetDirectory(DragEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source is null
            ? null
            : FindVisualAncestor<SelectorItem>(source);
        if (container is not null)
        {
            var entry =
                BrowserListView.ItemFromContainer(container) as FileSystemEntry
                ?? BrowserGridView.ItemFromContainer(container) as FileSystemEntry;
            if (entry is { IsDirectory: true }
                && Directory.Exists(entry.FullPath))
            {
                return entry.FullPath;
            }
        }

        return ViewModel.CurrentPath;
    }

    private async void RecentItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: FileSystemEntry entry })
        {
            return;
        }

        try
        {
            await ViewModel.OpenEntryAsync(entry);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or IOException)
        {
            ShowOperationError("Файл больше недоступен.");
        }
    }

    private void BrowserItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entries = sender switch
        {
            ListView listView => listView.SelectedItems.Cast<FileSystemEntry>().ToArray(),
            GridView gridView => gridView.SelectedItems.Cast<FileSystemEntry>().ToArray(),
            _ => []
        };

        if (entries.Length == 0)
        {
            ClosePreview();
            UpdateCommandAvailability();
            return;
        }

        _selectedEntry = entries[0];
        if (entries.Length == 1)
        {
            ShowPreview(entries[0]);
        }
        else
        {
            ShowMultipleSelectionPreview(entries);
        }

        UpdateCommandAvailability();
    }

    private void GamesGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is GridView { SelectedItem: GameEntry game })
        {
            _selectedGame = game;
            _selectedEntry = null;
            ShowGamePreview(game);
            return;
        }

        _selectedGame = null;
        ClosePreview();
    }

    private void GamesGridView_ItemClick(
        object sender,
        ItemClickEventArgs e)
    {
        if (sender is not GridView gridView
            || e.ClickedItem is not GameEntry game)
        {
            return;
        }

        gridView.SelectedItem = game;
        _selectedGame = game;
        _selectedEntry = null;
        ShowGamePreview(game);
    }

    private void GamesGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is GridView { SelectedItem: GameEntry game })
        {
            LaunchGame(game);
        }
    }

    private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameEntry game })
        {
            LaunchGame(game);
        }
    }

    private async void OpenGameFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameEntry game })
        {
            await NavigateToGameFilesAsync(game);
        }
    }

    private async void OpenSelectedGameFilesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedGame is not null)
        {
            await NavigateToGameFilesAsync(_selectedGame);
        }
    }

    private async Task NavigateToGameFilesAsync(GameEntry game)
    {
        if (!Directory.Exists(game.InstallPath))
        {
            ShowOperationError("Папка игры больше недоступна.");
            return;
        }

        _isHomeTabActive = false;
        _isWorkspaceTabActive = true;
        WorkspaceTabsScroller.Visibility = Visibility.Visible;
        ClearNavigationSelection();
        ClosePreview();
        await ViewModel.NavigateFolderAsync(game.InstallPath, game.Name);
        ViewModel.ReportStatus($"Файлы игры: {game.Name}");
    }

    private async void HideSelectedGameButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedGame is null)
        {
            return;
        }

        var game = _selectedGame;
        await ViewModel.HideGameAsync(game);
        ClosePreview();
        UpdateInterfaceState();
    }

    private async void RestoreHiddenGamesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        RestoreHiddenGamesButton.IsEnabled = false;
        try
        {
            await ViewModel.RestoreHiddenGamesAsync();
        }
        finally
        {
            RestoreHiddenGamesButton.IsEnabled = true;
            UpdateInterfaceState();
        }
    }

    private async void DeleteOrManageGameButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedGame is null)
        {
            return;
        }

        var game = _selectedGame;
        if (!game.IsLocalInstall)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        "ms-settings:appsfeatures")
                    {
                        UseShellExecute = true
                    });
                ViewModel.ReportStatus(
                    $"Открыто управление установкой: {game.Name}");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                ShowOperationError(
                    "Не удалось открыть список установленных приложений Windows.");
            }

            return;
        }

        if (!game.CanDeleteFiles)
        {
            await NavigateToGameFilesAsync(game);
            ViewModel.ReportStatus(
                "Проверьте папку игры. Удаление автоматически найденной папки отключено для безопасности.");
            return;
        }

        if (!CanDeleteLocalGame(game))
        {
            ShowOperationError(
                "Nexus не может безопасно удалить эту папку. Откройте файлы и проверьте расположение вручную.");
            return;
        }

        var torrentNote = string.IsNullOrWhiteSpace(game.RelatedTorrentPath)
            ? string.Empty
            : "\n\nФайл .torrent останется на месте.";
        var confirmation = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Удалить файлы «{game.Name}»?",
            Content = new TextBlock
            {
                Text =
                    $"Вся папка игры будет перемещена в Корзину Windows:\n{game.InstallPath}" +
                    torrentNote,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            PrimaryButtonText = "В корзину",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        DeleteGameFilesButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    game.InstallPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
            ClosePreview();
            ViewModel.ReportStatus($"Игра перемещена в Корзину: {game.Name}");
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ShowOperationError(
                "Windows не смогла переместить папку игры в Корзину.");
        }
        finally
        {
            DeleteGameFilesButton.IsEnabled = true;
        }
    }

    private static bool CanDeleteLocalGame(GameEntry game)
    {
        if (!game.IsLocalInstall
            || !game.CanDeleteFiles
            || !Directory.Exists(game.InstallPath)
            || string.IsNullOrWhiteSpace(game.LaunchTarget)
            || !File.Exists(game.LaunchTarget))
        {
            return false;
        }

        var installPath = Path.GetFullPath(game.InstallPath)
            .TrimEnd(Path.DirectorySeparatorChar);
        var executable = Path.GetFullPath(game.LaunchTarget);
        var root = Path.GetPathRoot(installPath)?
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(installPath, root, StringComparison.OrdinalIgnoreCase)
            || !IsSameOrAncestorPath(installPath, executable))
        {
            return false;
        }

        string[] protectedTrees =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        if (protectedTrees
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => IsSameOrAncestorPath(path, installPath)))
        {
            return false;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.Equals(
            Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar),
            installPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private async void AddGamesFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndAddGamesFolderAsync();
    }

    private async void GamesEmptyActionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.SearchQuery))
        {
            SetSearchBoxText(string.Empty);
            _searchDebounceTimer.Stop();
            await ViewModel.RefreshAsync();
            CaptureActiveSession();
            return;
        }

        await PickAndAddGamesFolderAsync();
    }

    private async Task<bool> PickAndAddGamesFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(
            picker,
            WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return false;
        }

        try
        {
            var added = await ViewModel.AddLocalGameFolderAsync(folder.Path);
            if (!added)
            {
                ViewModel.ReportStatus("Эта папка уже проверяется");
            }

            return added;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            ShowOperationError("Не удалось сохранить папку локальных игр.");
            return false;
        }
    }

    private void LaunchGame(GameEntry game)
    {
        try
        {
            ViewModel.LaunchGame(game);
            ViewModel.ReportStatus($"Запускается: {game.Name}");
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
            or DirectoryNotFoundException
            or FileNotFoundException)
        {
            ShowOperationError("Не удалось запустить игру или её лаунчер.");
        }
    }

    private async void OpenPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is not null)
        {
            LaunchGame(_selectedGame);
            return;
        }

        if (_selectedEntry is null)
        {
            return;
        }

        try
        {
            await ViewModel.OpenEntryAsync(_selectedEntry);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or IOException)
        {
            ShowOperationError("Не удалось открыть выбранный элемент.");
        }
    }

    private void ClosePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserListView.SelectedItems.Clear();
        BrowserGridView.SelectedItems.Clear();
        RecycleBinListView.SelectedItems.Clear();
        RecycleBinGridView.SelectedItems.Clear();
        RecentItemsList.SelectedItem = null;
        WorkspaceRecentItemsList.SelectedItem = null;
        GamesGridView.SelectedItem = null;
        ClosePreview();
        UpdateCommandAvailability();
        UpdateRecycleBinUiState();
    }

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = GetSelectedEntries();
            if (entries.Count == 0 && _selectedGame is not null)
            {
                var gamePackage = new DataPackage();
                gamePackage.SetText(_selectedGame.InstallPath);
                Clipboard.SetContent(gamePackage);
                ViewModel.ReportStatus("Путь к игре скопирован");
                return;
            }

            if (entries.Count == 0 && _selectedEntry is not null)
            {
                entries = [_selectedEntry];
            }

            if (entries.Count == 0)
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(string.Join(
                Environment.NewLine,
                entries.Select(item => item.FullPath)));
            Clipboard.SetContent(package);
            ViewModel.ReportStatus(entries.Count == 1
                ? "Путь скопирован"
                : $"Скопировано путей: {entries.Count}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowOperationError(
                "Буфер обмена занят другим приложением. Повторите попытку.");
        }
    }

    private async void NewFolderCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateFolderAsync();
    }

    private async void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await SetFileClipboardAsync(ClipboardOperation.Copy);
    }

    private async void CutCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await SetFileClipboardAsync(ClipboardOperation.Move);
    }

    private async void PasteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await PasteAsync();
    }

    private async void RenameCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await RenameSelectionAsync();
    }

    private async void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectionAsync();
    }

    private async void PropertiesCommandButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowPropertiesAsync();
    }

    private void SelectAllCommandButton_Click(object sender, RoutedEventArgs e)
    {
        SelectAllItems();
    }

    private async void FavoriteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries();
        if (entries.Count != 1)
        {
            return;
        }

        FavoriteCommandButton.IsEnabled = false;
        try
        {
            await ViewModel.ToggleFavoriteAsync(entries[0]);
            if (!ViewModel.IsFavorites)
            {
                UpdateCommandAvailability();
            }
            else
            {
                ClearBrowserSelection();
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException)
        {
            ShowOperationError("Не удалось сохранить список избранного.");
        }
        finally
        {
            FavoriteCommandButton.IsEnabled = true;
        }
    }

    private async Task CreateFolderAsync()
    {
        if (!CanModifyCurrentFolder())
        {
            return;
        }

        var suggestedName = GetAvailableFolderName(ViewModel.CurrentPath);
        var folderName = await ShowNameDialogAsync(
            "Новая папка",
            "Имя папки",
            suggestedName,
            "Создать");
        if (folderName is null)
        {
            return;
        }

        await RunFileOperationAsync(
            token => _fileOperationService.CreateFolderAsync(
                ViewModel.CurrentPath,
                folderName,
                token));
    }

    private async Task RenameSelectionAsync()
    {
        var entries = GetSelectedEntries();
        if (entries.Count != 1
            || !GetEntryCapabilities(entries[0]).CanRename)
        {
            return;
        }

        var entry = entries[0];
        var newName = await ShowNameDialogAsync(
            "Переименовать",
            "Новое имя",
            entry.Name,
            "Сохранить");
        if (newName is null)
        {
            return;
        }

        await RunFileOperationAsync(
            token => _fileOperationService.RenameAsync(
                entry.FullPath,
                newName,
                token));
    }

    private async Task SetFileClipboardAsync(ClipboardOperation operation)
    {
        var entries = GetSelectedEntries();
        if (entries.Count == 0
            || entries.Any(entry =>
                operation == ClipboardOperation.Move
                    ? !GetEntryCapabilities(entry).CanCut
                    : !GetEntryCapabilities(entry).CanCopy))
        {
            return;
        }

        var clipboardPaths = entries
            .Select(item => item.FullPath)
            .ToArray();

        try
        {
            var storageItems = new List<IStorageItem>(clipboardPaths.Length);
            foreach (var entry in entries)
            {
                storageItems.Add(entry.IsDirectory
                    ? await StorageFolder.GetFolderFromPathAsync(entry.FullPath)
                    : await StorageFile.GetFileFromPathAsync(entry.FullPath));
            }

            var package = new DataPackage
            {
                RequestedOperation = operation == ClipboardOperation.Move
                    ? DataPackageOperation.Move
                    : DataPackageOperation.Copy
            };
            package.SetStorageItems(storageItems, readOnly: false);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            _clipboardPaths = clipboardPaths;
            _clipboardOperation = operation;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or FileNotFoundException
            or IOException
            or System.Runtime.InteropServices.COMException)
        {
            ShowOperationError(
                "Не удалось передать выбранные элементы в системный буфер обмена.");
            return;
        }

        ViewModel.ReportStatus(operation == ClipboardOperation.Copy
            ? $"Для копирования: {_clipboardPaths.Count}"
            : $"Для перемещения: {_clipboardPaths.Count}");
        UpdateCommandAvailability();
    }

    private async Task PasteAsync()
    {
        if (!CanModifyCurrentFolder())
        {
            return;
        }

        await TryReadSystemClipboardAsync();
        if (_clipboardPaths.Count == 0)
        {
            ShowOperationError("В буфере обмена нет файлов или папок.");
            return;
        }

        var success = await RunTransferOperationAsync((options, progress, token) =>
            _clipboardOperation == ClipboardOperation.Move
                ? _fileOperationService.MoveAsync(
                    _clipboardPaths,
                    ViewModel.CurrentPath,
                    options,
                    progress,
                    token)
                : _fileOperationService.CopyAsync(
                    _clipboardPaths,
                    ViewModel.CurrentPath,
                    options,
                    progress,
                    token));
        if (success && _clipboardOperation == ClipboardOperation.Move)
        {
            _clipboardPaths = [];
            _clipboardOperation = ClipboardOperation.None;
            UpdateCommandAvailability();
        }
    }

    private async Task<bool> TryReadSystemClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.StorageItems))
            {
                return false;
            }

            var items = await content.GetStorageItemsAsync();
            var paths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return false;
            }

            _clipboardPaths = paths;
            _clipboardOperation = content.RequestedOperation == DataPackageOperation.Move
                ? ClipboardOperation.Move
                : ClipboardOperation.Copy;
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static bool HasSystemFileClipboard()
    {
        try
        {
            return Clipboard
                .GetContent()
                .Contains(StandardDataFormats.StorageItems);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DeleteSelectionAsync()
    {
        var entries = GetSelectedEntries();
        if (entries.Count == 0
            || entries.Any(entry => !GetEntryCapabilities(entry).CanDelete))
        {
            return;
        }

        var subject = entries.Count == 1
            ? $"«{entries[0].Name}»"
            : $"{entries.Count} элементов";
        var confirmation = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Переместить в корзину?",
            Content = new TextBlock
            {
                Text = $"{subject} будут перемещены в Корзину Windows. Их можно восстановить.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            PrimaryButtonText = "В корзину",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var deletionResult = await Task.Run(() =>
            {
                var moved = 0;
                var failed = new List<string>();
                foreach (var entry in entries)
                {
                    try
                    {
                        if (entry.IsDirectory)
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                entry.FullPath,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                        else
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                entry.FullPath,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }

                        moved++;
                    }
                    catch (Exception exception) when (
                        exception is UnauthorizedAccessException
                        or IOException
                        or System.ComponentModel.Win32Exception
                        or InvalidOperationException)
                    {
                        failed.Add(entry.Name);
                    }
                }

                return (Moved: moved, Failed: (IReadOnlyList<string>)failed);
            });

            ClearBrowserSelection();
            await ViewModel.RefreshAsync();
            if (deletionResult.Failed.Count == 0)
            {
                ViewModel.ReportStatus(
                    $"Перемещено в корзину: {deletionResult.Moved}");
            }
            else
            {
                var failedNames = string.Join(
                    ", ",
                    deletionResult.Failed.Take(3));
                ShowOperationError(
                    $"В корзину перемещено: {deletionResult.Moved}. " +
                    $"Не удалось: {deletionResult.Failed.Count} ({failedNames}).");
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            ShowOperationError("Windows не разрешила переместить выбранные элементы в корзину.");
        }
    }

    private async Task ShowPropertiesAsync()
    {
        var entries = GetSelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        string content;
        if (entries.Count == 1)
        {
            var entry = entries[0];
            content =
                $"Имя: {entry.Name}\n" +
                $"Тип: {entry.DisplayType}\n" +
                $"Расположение: {Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath}\n" +
                $"Размер: {(string.IsNullOrWhiteSpace(entry.DisplaySize) ? "—" : entry.DisplaySize)}\n" +
                $"Изменён: {entry.DisplayModified}";
        }
        else
        {
            var knownSize = entries
                .Where(item => item.SizeBytes is not null)
                .Sum(item => item.SizeBytes ?? 0);
            content =
                $"Выбрано: {entries.Count}\n" +
                $"Файлов: {entries.Count(item => !item.IsDirectory)}\n" +
                $"Папок: {entries.Count(item => item.IsDirectory)}\n" +
                $"Известный размер файлов: {FormatBytes(knownSize)}";
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = entries.Count == 1 ? "Свойства" : "Свойства выбранных элементов",
            Content = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            CloseButtonText = "Готово",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<string?> ShowNameDialogAsync(
        string title,
        string label,
        string initialValue,
        string primaryButtonText)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 360,
            SelectionStart = 0,
            SelectionLength = Path.GetFileNameWithoutExtension(initialValue).Length
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Foreground = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
                },
                textBox
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return textBox.Text;
    }

    private Task<bool> RunFileOperationAsync(
        Func<CancellationToken, Task<FileOperationResult>> operation) =>
        RunFileOperationCoreAsync(
            (_, _, token) => operation(token),
            supportsPause: false);

    private Task<bool> RunTransferOperationAsync(
        Func<
            FileOperationOptions,
            IProgress<FileOperationProgress>,
            CancellationToken,
            Task<FileOperationResult>> operation) =>
        RunFileOperationCoreAsync(operation, supportsPause: true);

    private async Task<bool> RunFileOperationCoreAsync(
        Func<
            FileOperationOptions,
            IProgress<FileOperationProgress>,
            CancellationToken,
            Task<FileOperationResult>> operation,
        bool supportsPause)
    {
        if (_fileOperationRunning)
        {
            ShowOperationError(
                "Дождитесь завершения текущей файловой операции или отмените её.");
            return false;
        }

        _operationCancellation = new CancellationTokenSource();
        _operationPauseController = supportsPause
            ? new FileOperationPauseController()
            : null;
        _fileOperationPaused = false;
        _fileOperationRunning = true;
        OperationProgressTextBlock.Text = supportsPause
            ? "Подготовка копирования…"
            : "Выполнение операции…";
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.Value = 0;
        PauseFileOperationButton.Content = "Пауза";
        var progress = new Progress<FileOperationProgress>(
            UpdateFileOperationProgress);
        var options = new FileOperationOptions
        {
            ConflictResolver = ResolveFileConflictAsync,
            PauseController = _operationPauseController
        };
        UpdateInterfaceState();
        try
        {
            var result = await operation(
                options,
                progress,
                _operationCancellation.Token);
            if (!result.Success)
            {
                ShowOperationError(result.Message);
                return false;
            }

            ViewModel.ReportStatus(result.Message);
            ClearBrowserSelection();
            await ViewModel.RefreshAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            ViewModel.ReportStatus(
                "Операция отменена. Уже завершённые действия не откатывались.");
            await ViewModel.RefreshAsync();
            return false;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            ShowOperationError(exception.Message);
            return false;
        }
        finally
        {
            _fileOperationRunning = false;
            _fileOperationPaused = false;
            _operationPauseController?.Resume();
            _operationPauseController = null;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            UpdateInterfaceState();
        }
    }

    private void UpdateFileOperationProgress(FileOperationProgress progress)
    {
        if (!_fileOperationRunning)
        {
            return;
        }

        var hasMeasurableTotal = progress.TotalBytes > 0
            || progress.TotalItems > 0
            || progress.Stage == FileOperationStage.Completed;
        OperationProgressBar.IsIndeterminate = !hasMeasurableTotal;
        if (hasMeasurableTotal)
        {
            OperationProgressBar.Value = progress.Percentage;
        }

        var action = progress.Stage switch
        {
            FileOperationStage.Preparing => "Подготовка",
            FileOperationStage.Copying => "Копирование",
            FileOperationStage.Moving => "Перемещение",
            FileOperationStage.DeletingSource => "Завершение переноса",
            FileOperationStage.Completed => "Готово",
            _ => "Файловая операция"
        };
        var currentName = string.IsNullOrWhiteSpace(progress.CurrentPath)
            ? string.Empty
            : Path.GetFileName(progress.CurrentPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        var measured = progress.TotalBytes > 0
            ? $"{FormatBytes(progress.ProcessedBytes)} из {FormatBytes(progress.TotalBytes)}"
            : progress.TotalItems > 0
                ? $"{progress.ProcessedItems} из {progress.TotalItems}"
                : string.Empty;
        var speed = progress.BytesPerSecond >= 1024
            ? $" · {FormatBytes((long)progress.BytesPerSecond)}/с"
            : string.Empty;
        OperationProgressTextBlock.Text = string.Join(
            " · ",
            new[] { action, currentName, measured }
                .Where(part => !string.IsNullOrWhiteSpace(part)))
            + speed;
    }

    private ValueTask<FileConflictResolution> ResolveFileConflictAsync(
        FileOperationConflict conflict,
        CancellationToken cancellationToken)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return new ValueTask<FileConflictResolution>(
                ShowFileConflictDialogAsync(conflict, cancellationToken));
        }

        var completion = new TaskCompletionSource<FileConflictResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(
                        await ShowFileConflictDialogAsync(
                            conflict,
                            cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetResult(FileConflictResolution.Fail);
        }

        return new ValueTask<FileConflictResolution>(completion.Task);
    }

    private async Task<FileConflictResolution> ShowFileConflictDialogAsync(
        FileOperationConflict conflict,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(conflict.DestinationPath);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Такой элемент уже существует",
            Content = $"В папке назначения уже есть «{name}». Что сделать?",
            PrimaryButtonText = conflict.IsDirectory
                ? "Сохранить обе"
                : "Заменить",
            SecondaryButtonText = conflict.IsDirectory
                ? string.Empty
                : "Сохранить обе",
            CloseButtonText = "Пропустить",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            ContentDialogResult.Primary when conflict.IsDirectory =>
                FileConflictResolution.KeepBoth,
            ContentDialogResult.Primary => FileConflictResolution.Replace,
            ContentDialogResult.Secondary => FileConflictResolution.KeepBoth,
            _ => FileConflictResolution.Skip
        };
    }

    private void PauseFileOperationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationPauseController is null
            || !_fileOperationRunning)
        {
            return;
        }

        if (_fileOperationPaused)
        {
            _operationPauseController.Resume();
            _fileOperationPaused = false;
            PauseFileOperationButton.Content = "Пауза";
            ViewModel.ReportStatus("Файловая операция продолжена.");
        }
        else
        {
            _operationPauseController.Pause();
            _fileOperationPaused = true;
            PauseFileOperationButton.Content = "Продолжить";
            ViewModel.ReportStatus("Файловая операция приостановлена.");
        }

        UpdateInterfaceState();
    }

    private void CancelFileOperationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        ViewModel.ReportStatus("Отмена операции…");
        UpdateInterfaceState();
    }

    private void OpenInExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedGame is not null)
            {
                ViewModel.OpenGameFolder(_selectedGame);
                return;
            }

            ViewModel.OpenInExplorer(_selectedEntry);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError("Не удалось открыть стандартный Проводник.");
        }
    }

    private void ExplorerFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.OpenInExplorer();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowOperationError("Не удалось открыть стандартный Проводник.");
        }
    }

    private async void RecycleNavigationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CaptureActiveSession();
        _isRecycleBinActive = true;
        _searchDebounceTimer.Stop();
        ClearOperationError();
        ClosePreview();
        SetNavigationSelection(RecycleNavigationButton);
        SetSearchBoxText(string.Empty);
        ApplyRecycleBinFilter(string.Empty);
        UpdateInterfaceState();
        await LoadRecycleBinAsync();
    }

    private async Task LoadRecycleBinAsync()
    {
        _recycleBinLoadCancellation?.Cancel();
        _recycleBinLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _recycleBinLoadCancellation = cancellation;
        _isRecycleBinLoading = true;
        _recycleBinErrorMessage = null;
        UpdateRecycleBinUiState();

        try
        {
            var entries = await _recycleBinService.GetEntriesAsync(
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !ReferenceEquals(_recycleBinLoadCancellation, cancellation))
            {
                return;
            }

            _allRecycleBinItems = entries
                .Select(entry => new RecycleBinListItem(entry))
                .ToArray();
            RecycleBinListView.SelectedItems.Clear();
            RecycleBinGridView.SelectedItems.Clear();
            ClosePreview();
            ApplyRecycleBinFilter(SearchBox.Text);
            ViewModel.ReportStatus(
                _allRecycleBinItems.Count == 0
                    ? "Корзина пуста"
                    : $"В Корзине {FormatRecycleItemCount(_allRecycleBinItems.Count)}");
        }
        catch (OperationCanceledException)
        {
            if (_isRecycleBinActive)
            {
                ViewModel.ReportStatus("Чтение Корзины отменено");
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                _recycleBinErrorMessage =
                    "Windows не предоставила содержимое Корзины. " +
                    "Проверьте доступ к дискам и повторите попытку.";
            }
        }
        finally
        {
            if (ReferenceEquals(_recycleBinLoadCancellation, cancellation))
            {
                _recycleBinLoadCancellation = null;
                _isRecycleBinLoading = false;
                cancellation.Dispose();
                UpdateRecycleBinUiState();
            }
        }
    }

    private void ApplyRecycleBinFilter(string query)
    {
        var normalizedQuery = query.Trim();
        var selectedIds = GetSelectedRecycleBinItems()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = string.IsNullOrWhiteSpace(normalizedQuery)
            ? _allRecycleBinItems
            : _allRecycleBinItems
                .Where(item =>
                    item.Name.Contains(
                        normalizedQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || item.OriginalPathDisplay.Contains(
                        normalizedQuery,
                        StringComparison.CurrentCultureIgnoreCase)
                    || item.DisplayType.Contains(
                        normalizedQuery,
                        StringComparison.CurrentCultureIgnoreCase))
                .ToArray();

        RecycleBinItems.Clear();
        foreach (var item in filtered)
        {
            RecycleBinItems.Add(item);
        }

        RestoreRecycleBinSelection(selectedIds);
        UpdateRecycleBinUiState();
    }

    private async void RefreshRecycleBinCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadRecycleBinAsync();
    }

    private async void RetryRecycleBinButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadRecycleBinAsync();
    }

    private void CancelRecycleBinLoadButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _recycleBinLoadCancellation?.Cancel();
    }

    private void RecycleBinViewModeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selectedIds = GetSelectedRecycleBinItems()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        _recycleBinGridMode = !_recycleBinGridMode;
        RecycleBinListView.SelectedItems.Clear();
        RecycleBinGridView.SelectedItems.Clear();
        RestoreRecycleBinSelection(selectedIds);
        UpdateRecycleBinUiState();
    }

    private void SelectAllRecycleBinCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_isRecycleBinLoading && RecycleBinItems.Count > 0)
        {
            GetActiveRecycleBinView().SelectAll();
        }
    }

    private void RecycleBinItems_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ListViewBase view
            || !ReferenceEquals(view, GetActiveRecycleBinView()))
        {
            return;
        }

        var selected = GetSelectedRecycleBinItems();
        if (selected.Count == 1)
        {
            ShowRecycleBinPreview(selected[0]);
        }
        else if (selected.Count > 1)
        {
            ShowMultipleRecycleBinPreview(selected);
        }
        else
        {
            ClosePreview();
        }

        UpdateRecycleBinUiState();
    }

    private void RecycleBinItems_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase view)
        {
            return;
        }

        if (e.OriginalSource is FrameworkElement
            {
                DataContext: RecycleBinListItem clickedItem
            }
            && !view.SelectedItems.Contains(clickedItem))
        {
            view.SelectedItems.Clear();
            view.SelectedItem = clickedItem;
        }

        if (GetSelectedRecycleBinItems().Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout();
        var restoreItem = new MenuFlyoutItem
        {
            Text = "Восстановить",
            Icon = new FontIcon { Glyph = "\uE777" }
        };
        restoreItem.Click += RestoreRecycleBinCommandButton_Click;
        menu.Items.Add(restoreItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Удалить безвозвратно…",
            Foreground = new SolidColorBrush(
                ColorHelper.FromArgb(255, 255, 128, 128)),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += DeleteRecycleBinCommandButton_Click;
        menu.Items.Add(deleteItem);
        menu.ShowAt(view, e.GetPosition(view));
        e.Handled = true;
    }

    private async void RestoreRecycleBinCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected = GetSelectedRecycleBinItems();
        if (selected.Count == 0)
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            token => _recycleBinService.RestoreAsync(
                selected.Select(item => item.Id).ToArray(),
                token),
            selected.Count == 1
                ? $"Восстановлен «{selected[0].Name}»"
                : $"Восстановлено {FormatRecycleItemCount(selected.Count)}");
    }

    private async void DeleteRecycleBinCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected = GetSelectedRecycleBinItems();
        if (selected.Count == 0
            || !await ConfirmPermanentRecycleBinDeletionAsync(selected))
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            token => _recycleBinService.DeletePermanentlyAsync(
                selected.Select(item => item.Id).ToArray(),
                token),
            selected.Count == 1
                ? $"Безвозвратно удалён «{selected[0].Name}»"
                : $"Безвозвратно удалено {FormatRecycleItemCount(selected.Count)}");
    }

    private async void EmptyRecycleBinCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var count = _allRecycleBinItems.Count;
        if (count == 0 || !await ConfirmEmptyRecycleBinAsync(count))
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            token => _recycleBinService.EmptyAsync(token),
            "Корзина очищена");
    }

    private async Task RunRecycleBinOperationAsync(
        Func<CancellationToken, Task> operation,
        string successMessage)
    {
        if (_isRecycleBinOperationRunning)
        {
            return;
        }

        _recycleBinOperationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _recycleBinOperationCancellation = cancellation;
        _isRecycleBinOperationRunning = true;
        _recycleBinErrorMessage = null;
        UpdateRecycleBinUiState();

        try
        {
            await operation(cancellation.Token);
            ViewModel.ReportStatus(successMessage);
            if (_isRecycleBinActive)
            {
                await LoadRecycleBinAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ViewModel.ReportStatus("Операция с Корзиной отменена");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            _recycleBinErrorMessage =
                "Windows не завершила операцию. Содержимое Корзины не изменено " +
                "или изменено не полностью — обновите список и проверьте элементы.";
        }
        finally
        {
            if (ReferenceEquals(_recycleBinOperationCancellation, cancellation))
            {
                _recycleBinOperationCancellation = null;
                cancellation.Dispose();
            }

            _isRecycleBinOperationRunning = false;
            UpdateRecycleBinUiState();
        }
    }

    private async Task<bool> ConfirmPermanentRecycleBinDeletionAsync(
        IReadOnlyList<RecycleBinListItem> selected)
    {
        var count = selected.Count;
        var detail = count == 1
            ? $"Файл или папка «{selected[0].Name}» будет удалён безвозвратно."
            : $"{count} выбранных элементов будут удалены безвозвратно.";
        return await ShowRecycleBinDangerDialogAsync(
            count == 1
                ? "Удалить элемент безвозвратно?"
                : $"Удалить {count} элементов безвозвратно?",
            detail,
            count,
            "Удалить безвозвратно");
    }

    private Task<bool> ConfirmEmptyRecycleBinAsync(int count)
    {
        return ShowRecycleBinDangerDialogAsync(
            "Очистить Корзину?",
            $"Все элементы в Корзине ({count}) будут удалены безвозвратно.",
            count,
            "Очистить");
    }

    private async Task<bool> ShowRecycleBinDangerDialogAsync(
        string title,
        string detail,
        int exactCount,
        string primaryButtonText)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Будет удалено: {exactCount}. Это действие нельзя отменить.",
            Foreground = new SolidColorBrush(
                ColorHelper.FromArgb(255, 255, 128, 128)),
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void BackAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isRecycleBinActive && !_isSettingsActive && ViewModel.CanGoBack)
        {
            args.Handled = true;
            ClosePreview();
            await ViewModel.GoBackAsync();
        }
    }

    private async void ForwardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isRecycleBinActive && !_isSettingsActive && ViewModel.CanGoForward)
        {
            args.Handled = true;
            ClosePreview();
            await ViewModel.GoForwardAsync();
        }
    }

    private async void UpAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isRecycleBinActive && !_isSettingsActive && ViewModel.CanNavigateUp)
        {
            args.Handled = true;
            ClosePreview();
            await ViewModel.GoUpAsync();
        }
    }

    private async void RefreshAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ClosePreview();
        if (_isSettingsActive)
        {
            RefreshSettingsPage();
            return;
        }

        if (_isRecycleBinActive)
        {
            await LoadRecycleBinAsync();
            return;
        }

        await ViewModel.RefreshAsync();
    }

    private async void NewTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CreateWorkspaceTabAsync();
    }

    private async void CloseTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isWorkspaceTabActive)
        {
            return;
        }

        args.Handled = true;
        await CloseActiveWorkspaceTabAsync();
    }

    private async void NextTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CycleTabAsync(reverse: false);
    }

    private async void PreviousTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CycleTabAsync(reverse: true);
    }

    private async Task CycleTabAsync(bool reverse)
    {
        var tabCount = _workspaceTabs.Count + 2;
        var currentIndex = _isHomeTabActive
            ? 0
            : !_isWorkspaceTabActive
                ? 1
                : _activeWorkspaceTab is null
                    ? 1
                    : _workspaceTabs.IndexOf(_activeWorkspaceTab) + 2;
        var nextIndex = reverse
            ? (currentIndex - 1 + tabCount) % tabCount
            : (currentIndex + 1) % tabCount;

        if (nextIndex == 0)
        {
            await ActivateHomeTabAsync();
        }
        else if (nextIndex == 1)
        {
            await ActivateDashboardTabAsync();
        }
        else
        {
            await ActivateWorkspaceTabAsync(_workspaceTabs[nextIndex - 2]);
        }
    }

    private void SearchAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_isSettingsActive
            || (!_isRecycleBinActive && !ViewModel.CanSearch))
        {
            return;
        }

        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void AddressAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isRecycleBinActive || _isSettingsActive)
        {
            args.Handled = true;
            return;
        }

        args.Handled = true;
        AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    private void SelectAllAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused())
        {
            return;
        }

        if (_isRecycleBinActive)
        {
            args.Handled = true;
            SelectAllRecycleBinCommandButton_Click(sender, new RoutedEventArgs());
            return;
        }

        if (BrowserView.Visibility != Visibility.Visible)
        {
            return;
        }

        args.Handled = true;
        SelectAllItems();
    }

    private async void CopyAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || GetSelectedEntries().Count == 0)
        {
            return;
        }

        args.Handled = true;
        await SetFileClipboardAsync(ClipboardOperation.Copy);
    }

    private async void CutAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || GetSelectedEntries().Count == 0)
        {
            return;
        }

        args.Handled = true;
        await SetFileClipboardAsync(ClipboardOperation.Move);
    }

    private async void PasteAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()
            || !CanModifyCurrentFolder())
        {
            return;
        }

        args.Handled = true;
        await PasteAsync();
    }

    private async void RenameAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || GetSelectedEntries().Count != 1)
        {
            return;
        }

        args.Handled = true;
        await RenameSelectionAsync();
    }

    private async void DeleteAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsTextInputFocused()
            && _isRecycleBinActive
            && GetSelectedRecycleBinItems().Count > 0)
        {
            args.Handled = true;
            DeleteRecycleBinCommandButton_Click(sender, new RoutedEventArgs());
            return;
        }

        if (IsTextInputFocused() || GetSelectedEntries().Count == 0)
        {
            return;
        }

        args.Handled = true;
        await DeleteSelectionAsync();
    }

    private async void NewFolderAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || !CanModifyCurrentFolder())
        {
            return;
        }

        args.Handled = true;
        await CreateFolderAsync();
    }

    private async void OpenAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsTextInputFocused()
            && ViewModel.IsGames
            && GamesGridView.SelectedItem is GameEntry game)
        {
            args.Handled = true;
            LaunchGame(game);
            return;
        }

        if (IsTextInputFocused()
            || BrowserView.Visibility != Visibility.Visible
            || GetSelectedEntries().Count != 1)
        {
            return;
        }

        args.Handled = true;
        await OpenSelectedEntryAsync();
    }

    private async void PropertiesAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()
            || BrowserView.Visibility != Visibility.Visible
            || GetSelectedEntries().Count == 0)
        {
            return;
        }

        args.Handled = true;
        await ShowPropertiesAsync();
    }

    private async void BackspaceAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isRecycleBinActive
            || _isSettingsActive
            || IsTextInputFocused()
            || !ViewModel.CanGoBack)
        {
            return;
        }

        args.Handled = true;
        ClosePreview();
        await ViewModel.GoBackAsync();
    }

    private void ContextMenuAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (BrowserView.Visibility != Visibility.Visible
            || IsTextInputFocused())
        {
            return;
        }

        args.Handled = true;
        ShowBrowserContextMenu(
            _isGridMode
                ? BrowserGridView
                : BrowserListView);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentPath)
            or nameof(MainWindowViewModel.CurrentTitle)
            or nameof(MainWindowViewModel.CurrentKind))
        {
            CaptureActiveSession();
        }

        QueueInterfaceUpdate();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueInterfaceUpdate();
    }

    private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueInterfaceUpdate();
    }

    private void RecentItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueInterfaceUpdate();
    }

    private void QueueInterfaceUpdate()
    {
        if (_interfaceUpdatePending)
        {
            return;
        }

        _interfaceUpdatePending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _interfaceUpdatePending = false;
                UpdateInterfaceState();
            }))
        {
            _interfaceUpdatePending = false;
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compactSidebar = e.NewSize.Width < 1120;
        var sidebarWidth = compactSidebar
            ? 72
            : e.NewSize.Width < 1320
                ? 204
                : e.NewSize.Width >= 1900
                    ? 240
                    : 228;
        SidebarColumn.Width = new GridLength(sidebarWidth);
        TitleSidebarColumn.Width = new GridLength(sidebarWidth);
        var compactChrome = e.NewSize.Width < 980;
        AppTitleText.Visibility =
            compactChrome ? Visibility.Collapsed : Visibility.Visible;
        HomeTabLabel.Visibility =
            compactChrome ? Visibility.Collapsed : Visibility.Visible;
        HomeTabButton.MinWidth = compactChrome ? 48 : 156;
        HomeTabButton.Padding = compactChrome
            ? new Thickness(0)
            : new Thickness(14, 0, 14, 0);
        DashboardTabButton.MinWidth = compactChrome ? 140 : 206;
        SearchColumn.Width = new GridLength(
            e.NewSize.Width >= 1900
                ? 360
                : e.NewSize.Width < 900
                    ? 160
                    : e.NewSize.Width < 1120
                        ? 220
                        : 300);
        var availableTabWidth =
            e.NewSize.Width
            - sidebarWidth
            - HomeTabButton.MinWidth
            - DashboardTabButton.MinWidth
            - 42
            - 170;
        WorkspaceTabsScroller.MaxWidth = Math.Min(
            e.NewSize.Width >= 1900 ? 1000 : 620,
            Math.Max(190, availableTabWidth));
        HomeContentPanel.MaxWidth = e.NewSize.Width >= 1900
            ? 1680
            : 1360;
        RecycleBinContentHost.MaxWidth = e.NewSize.Width >= 1900
            ? 2100
            : 1800;
        var stackSettingsCards = e.NewSize.Width < 1180;
        SettingsSecondaryColumn.Width = stackSettingsCards
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SourceSettingsCard, stackSettingsCards ? 0 : 1);
        Grid.SetRow(SourceSettingsCard, stackSettingsCards ? 1 : 0);

        foreach (var label in _navigationLabels)
        {
            label.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        }
        SpacesNavigationHeader.Visibility =
            compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        StorageNavigationHeader.Visibility =
            compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        AiNavigationHeader.Visibility =
            compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        AiNavigationSeparator.Visibility =
            compactSidebar ? Visibility.Collapsed : Visibility.Visible;

        foreach (var button in _navigationButtons)
        {
            button.HorizontalContentAlignment = compactSidebar
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
        }

        RecycleNavigationButton.HorizontalContentAlignment = compactSidebar
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        SettingsNavigationButton.HorizontalContentAlignment = compactSidebar
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        ExplorerFallbackButton.HorizontalContentAlignment = compactSidebar
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;

        if (e.NewSize.Width < 1320 && PreviewPanel.Visibility == Visibility.Visible)
        {
            ClosePreview();
        }

        UpdateDashboardSpaceLayout();
    }

    private async void HomeTabButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateHomeTabAsync();
    }

    private async Task ActivateHomeTabAsync()
    {
        CaptureActiveSession();
        _isHomeTabActive = true;
        _isWorkspaceTabActive = false;
        SetNavigationSelection(HomeNavigationButton);
        if (_homeSession is null)
        {
            await ViewModel.NavigateToTargetAsync("home");
            _homeSession = ViewModel.CaptureSession();
        }
        else
        {
            if (!await ViewModel.RestoreSessionAsync(_homeSession))
            {
                await ViewModel.NavigateToTargetAsync("home");
                _homeSession = ViewModel.CaptureSession();
                ShowOperationError(
                    "Предыдущее расположение главной вкладки недоступно. Открыта Главная.");
            }
        }

        ClosePreview();
        UpdateInterfaceState();
    }

    private async void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateWorkspaceTabAsync();
    }

    private async void DashboardTabButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateDashboardTabAsync();
    }

    private async Task ActivateDashboardTabAsync()
    {
        CaptureActiveSession();
        _isHomeTabActive = false;
        _isWorkspaceTabActive = false;
        SetNavigationSelection(ProjectsNavigationButton);
        if (_dashboardSession is null)
        {
            await ViewModel.NavigateToTargetAsync("projects");
            _dashboardSession = ViewModel.CaptureSession();
        }
        else
        {
            if (!await ViewModel.RestoreSessionAsync(_dashboardSession))
            {
                await ViewModel.NavigateToTargetAsync("projects");
                _dashboardSession = ViewModel.CaptureSession();
                ShowOperationError(
                    "Предыдущее расположение вкладки недоступно. Открыты Проекты.");
            }
        }

        ClosePreview();
        UpdateInterfaceState();
    }

    private void SettingsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureActiveSession();
        _isSettingsActive = true;
        _searchDebounceTimer.Stop();
        ClearOperationError();
        ClosePreview();
        SetNavigationSelection(SettingsNavigationButton);
        SetSearchBoxText(string.Empty);
        RefreshSettingsPage();
        UpdateInterfaceState();
        ViewModel.ReportStatus("Настройки Nexus");
    }

    private async void LegacySettingsDialog_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly
            .GetName()
            .Version?
            .ToString(3)
            ?? "0.8.0";
        var settingsErrorBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = InfoBarSeverity.Error,
            Title = "Настройка не сохранена"
        };
        void ShowSettingsError(string message)
        {
            settingsErrorBar.Message = message;
            settingsErrorBar.IsOpen = true;
        }

        var hiddenItemsCheckBox = new CheckBox
        {
            MinHeight = 40,
            Content = "Показывать скрытые элементы",
            IsChecked = ViewModel.ShowHiddenItems
        };
        hiddenItemsCheckBox.Click += async (_, _) =>
        {
            hiddenItemsCheckBox.IsEnabled = false;
            try
            {
                await ViewModel.SetShowHiddenItemsAsync(
                    hiddenItemsCheckBox.IsChecked == true);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                or IOException
                or InvalidOperationException)
            {
                hiddenItemsCheckBox.IsChecked = ViewModel.ShowHiddenItems;
                ShowSettingsError(
                    "Не удалось сохранить настройку скрытых элементов.");
            }
            finally
            {
                hiddenItemsCheckBox.IsEnabled = true;
            }
        };
        var sourcesPanel = new StackPanel
        {
            Spacing = 8
        };
        var configuredSources = ViewModel.GetLocalGameFolders();
        if (configuredSources.Count == 0)
        {
            sourcesPanel.Children.Add(new TextBlock
            {
                Text = "Дополнительные папки не добавлены. Nexus всё равно проверяет стандартные библиотеки, Загрузки, Рабочий стол и фиксированные диски.",
                TextWrapping = TextWrapping.Wrap,
                Foreground =
                    (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
            });
        }
        else
        {
            foreach (var source in configuredSources)
            {
                var row = new Grid
                {
                    MinHeight = 44,
                    ColumnSpacing = 8
                };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto
                });
                var sourceLabel = new TextBlock
                {
                    Text = source,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                ToolTipService.SetToolTip(sourceLabel, source);
                row.Children.Add(sourceLabel);
                var removeButton = new Button
                {
                    MinWidth = 84,
                    MinHeight = 40,
                    Content = "Убрать",
                    CornerRadius = new CornerRadius(8),
                    Tag = source
                };
                removeButton.Click += async (_, _) =>
                {
                    removeButton.IsEnabled = false;
                    try
                    {
                        if (await ViewModel.RemoveLocalGameFolderAsync(source))
                        {
                            row.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch (Exception exception) when (
                        exception is UnauthorizedAccessException
                        or IOException
                        or InvalidOperationException)
                    {
                        ShowSettingsError(
                            "Не удалось изменить список игровых источников.");
                    }
                    finally
                    {
                        removeButton.IsEnabled = true;
                    }
                };
                Grid.SetColumn(removeButton, 1);
                row.Children.Add(removeButton);
                sourcesPanel.Children.Add(row);
            }
        }

        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 560,
            Children =
            {
                new TextBlock
                {
                    Text = $"Nexus Shell {version}",
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                settingsErrorBar,
                new TextBlock
                {
                    Text = "Безопасный режим файлов",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "Автоматическое перемещение отключено. Nexus меняет файлы только по вашей команде, а удаление выполняет через Корзину Windows.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
                },
                hiddenItemsCheckBox,
                new TextBlock
                {
                    Text = "Игры и торренты",
                    Margin = new Thickness(0, 8, 0, 0),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "Добавленные папки участвуют в поиске локальных игр и .torrent. Удаление источника из списка не удаляет файлы.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
                },
                sourcesPanel,
                new TextBlock
                {
                    Text = "Быстрые команды",
                    Margin = new Thickness(0, 8, 0, 0),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "Ctrl+L — адрес  ·  Ctrl+F — поиск  ·  Ctrl+T — новая вкладка\nCtrl+C / X / V — файлы  ·  F2 — переименовать  ·  Delete — в Корзину",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
                },
                new TextBlock
                {
                    Text = "Автозапуск можно включить при установке или изменить в Параметрах Windows → Приложения → Автозагрузка.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"]
                }
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Настройки Nexus",
            Content = content,
            SecondaryButtonText = "Добавить папку игр",
            CloseButtonText = "Готово",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
        {
            await PickAndAddGamesFolderAsync();
        }
    }

    private void RefreshSettingsPage()
    {
        var version = typeof(MainWindow).Assembly
            .GetName()
            .Version?
            .ToString(3)
            ?? "0.8.0";
        SettingsVersionText.Text = $"Nexus Shell {version}";
        SettingsShowHiddenItemsCheckBox.IsChecked = ViewModel.ShowHiddenItems;
        SettingsShowSystemApplicationsCheckBox.IsChecked =
            ViewModel.ShowSystemApplications;
        SettingsApplicationsHintText.Text =
            ViewModel.HiddenSystemApplicationsCount > 0
                ? $"Скрыто системных приложений: {ViewModel.HiddenSystemApplicationsCount}."
                : "По умолчанию видны только обычные установленные программы.";

        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexus Shell");
        SettingsDataPathText.Text = $"Локальные настройки: {dataPath}";
        ToolTipService.SetToolTip(SettingsDataPathText, dataPath);

        SettingsGameSourcesPanel.Children.Clear();
        var gameSources = ViewModel.GetLocalGameFolders();
        if (gameSources.Count == 0)
        {
            SettingsGameSourcesPanel.Children.Add(
                CreateSettingsEmptySourceText(
                    "Дополнительные папки не добавлены."));
        }
        else
        {
            foreach (var source in gameSources)
            {
                SettingsGameSourcesPanel.Children.Add(
                    CreateSettingsSourceRow(
                        source,
                        SettingsRemoveGameSourceButton_Click));
            }
        }

        SettingsAiSourcesPanel.Children.Clear();
        IReadOnlyList<string> aiSources;
        try
        {
            aiSources = _aiWorkspaceService.GetCustomSearchRoots();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            aiSources = [];
            ShowSettingsMessage(
                "Не удалось прочитать пользовательские папки AI-поиска.",
                InfoBarSeverity.Error);
        }

        if (aiSources.Count == 0)
        {
            SettingsAiSourcesPanel.Children.Add(
                CreateSettingsEmptySourceText(
                    "Используются стандартные рабочие папки Windows и Codex."));
        }
        else
        {
            foreach (var source in aiSources)
            {
                SettingsAiSourcesPanel.Children.Add(
                    CreateSettingsSourceRow(
                        source,
                        SettingsRemoveAiSourceButton_Click));
            }
        }
    }

    private static TextBlock CreateSettingsEmptySourceText(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            Foreground =
                (Brush)Application.Current.Resources["NexusTextTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };

    private static Grid CreateSettingsSourceRow(
        string path,
        RoutedEventHandler removeHandler)
    {
        var row = new Grid
        {
            MinHeight = 42,
            Padding = new Thickness(10, 0, 4, 0),
            Background =
                (Brush)Application.Current.Resources["NexusInsetBrush"],
            CornerRadius = new CornerRadius(8),
            ColumnSpacing = 8
        };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var pathText = new TextBlock
        {
            Text = path,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(pathText, path);
        row.Children.Add(pathText);

        var removeButton = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 11
            },
            Tag = path
        };
        AutomationProperties.SetName(
            removeButton,
            $"Убрать источник {path}");
        ToolTipService.SetToolTip(removeButton, "Убрать из списка");
        removeButton.Click += removeHandler;
        Grid.SetColumn(removeButton, 1);
        row.Children.Add(removeButton);
        return row;
    }

    private async void SettingsShowHiddenItemsCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsShowHiddenItemsCheckBox.IsEnabled = false;
        try
        {
            await ViewModel.SetShowHiddenItemsAsync(
                SettingsShowHiddenItemsCheckBox.IsChecked == true);
            ShowSettingsMessage(
                "Отображение скрытых элементов обновлено.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException)
        {
            SettingsShowHiddenItemsCheckBox.IsChecked =
                ViewModel.ShowHiddenItems;
            ShowSettingsMessage(
                "Не удалось сохранить настройку скрытых элементов.",
                InfoBarSeverity.Error);
        }
        finally
        {
            SettingsShowHiddenItemsCheckBox.IsEnabled = true;
        }
    }

    private void SettingsShowSystemApplicationsCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        var saved = ViewModel.SetShowSystemApplications(
            SettingsShowSystemApplicationsCheckBox.IsChecked == true);
        RefreshSettingsPage();
        ShowSettingsMessage(
            saved
                ? ViewModel.ShowSystemApplications
                    ? "Системные приложения будут показаны в каталоге."
                    : "Системные приложения скрыты из основного каталога."
                : "Настройка применена только на текущий сеанс: файл параметров недоступен.",
            saved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void SettingsAddGameFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (await PickAndAddGamesFolderAsync())
        {
            RefreshSettingsPage();
            ShowSettingsMessage(
                "Папка добавлена в источники игр и торрентов.",
                InfoBarSeverity.Success);
        }
    }

    private async void SettingsRemoveGameSourceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await ViewModel.RemoveLocalGameFolderAsync(path);
            RefreshSettingsPage();
            ShowSettingsMessage(
                "Источник игр убран. Файлы на диске не изменены.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException)
        {
            button.IsEnabled = true;
            ShowSettingsMessage(
                "Не удалось изменить список игровых источников.",
                InfoBarSeverity.Error);
        }
    }

    private async void SettingsAddAiFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var folder = await PickSettingsFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            var added = _aiWorkspaceService.AddSearchRoot(folder.Path);
            RefreshSettingsPage();
            var message = added
                ? "Папка добавлена в поиск AI-проектов."
                : "Эта папка уже участвует в поиске AI-проектов.";
            if (_isSettingsActive)
            {
                ShowSettingsMessage(
                    message,
                    added
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Informational);
            }
            else
            {
                ViewModel.ReportStatus(message);
            }
            await LoadAiCenterMetadataAsync(force: true);
            if (ViewModel.CurrentKind == NavigationKind.AiProjects)
            {
                await ViewModel.RefreshAsync();
                CaptureActiveSession();
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            if (_isSettingsActive)
            {
                ShowSettingsMessage(
                    "Не удалось сохранить папку поиска AI-проектов.",
                    InfoBarSeverity.Error);
            }
            else
            {
                ShowOperationError(
                    "Не удалось сохранить папку поиска AI-проектов.");
            }
        }
    }

    private void AiCenterAddFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsAddAiFolderButton_Click(sender, e);
    }

    private async void SettingsRemoveAiSourceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            _aiWorkspaceService.RemoveSearchRoot(path);
            RefreshSettingsPage();
            ShowSettingsMessage(
                "Папка убрана из поиска AI-проектов. Файлы не изменены.",
                InfoBarSeverity.Success);
            await LoadAiCenterMetadataAsync(force: true);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            button.IsEnabled = true;
            ShowSettingsMessage(
                "Не удалось изменить папки поиска AI-проектов.",
                InfoBarSeverity.Error);
        }
    }

    private async Task<StorageFolder?> PickSettingsFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(
            picker,
            WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFolderAsync();
    }

    private void SettingsOpenStartupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:startupapps")
                {
                    UseShellExecute = true
                });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowSettingsMessage(
                "Не удалось открыть настройки автозагрузки Windows.",
                InfoBarSeverity.Error);
        }
    }

    private void ShowSettingsMessage(
        string message,
        InfoBarSeverity severity)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }

    private void UpdateRecycleBinUiState()
    {
        if (RecycleBinView is null)
        {
            return;
        }

        var hasItems = RecycleBinItems.Count > 0;
        var showItems = _isRecycleBinActive
            && !_isRecycleBinLoading
            && hasItems;
        RecycleBinListView.Visibility =
            showItems && !_recycleBinGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecycleBinListHeader.Visibility =
            showItems && !_recycleBinGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecycleBinGridView.Visibility =
            showItems && _recycleBinGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecycleBinLoadingState.Visibility =
            _isRecycleBinActive && _isRecycleBinLoading
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecycleBinEmptyState.Visibility =
            _isRecycleBinActive
            && !_isRecycleBinLoading
            && !hasItems
                ? Visibility.Visible
                : Visibility.Collapsed;

        var hasError = !string.IsNullOrWhiteSpace(_recycleBinErrorMessage);
        RecycleBinErrorBar.Message = _recycleBinErrorMessage ?? string.Empty;
        RecycleBinErrorBar.IsOpen = _isRecycleBinActive && hasError;
        if (hasError)
        {
            RecycleBinEmptyTitle.Text = "Не удалось прочитать Корзину";
            RecycleBinEmptyDescription.Text = _recycleBinErrorMessage!;
        }
        else if (!string.IsNullOrWhiteSpace(SearchBox.Text)
                 && _allRecycleBinItems.Count > 0)
        {
            RecycleBinEmptyTitle.Text = "Ничего не найдено";
            RecycleBinEmptyDescription.Text =
                $"В Корзине нет совпадений по запросу «{SearchBox.Text.Trim()}».";
        }
        else
        {
            RecycleBinEmptyTitle.Text = "Корзина пуста";
            RecycleBinEmptyDescription.Text =
                "Удалённые через Nexus и Windows файлы появятся здесь.";
        }

        RecycleBinCountText.Text =
            RecycleBinItems.Count != _allRecycleBinItems.Count
            && !string.IsNullOrWhiteSpace(SearchBox.Text)
                ? $"{RecycleBinItems.Count} из {_allRecycleBinItems.Count}"
                : FormatRecycleItemCount(_allRecycleBinItems.Count);
        RecycleBinViewModeButton.Label =
            _recycleBinGridMode ? "Список" : "Плитки";
        RecycleBinViewModeButton.Icon = new FontIcon
        {
            Glyph = _recycleBinGridMode ? "\uE8A5" : "\uF0E2"
        };

        var selected = GetSelectedRecycleBinItems();
        var interactionEnabled = _isRecycleBinActive
            && !_isRecycleBinLoading
            && !_isRecycleBinOperationRunning;
        RestoreRecycleBinCommandButton.IsEnabled =
            interactionEnabled && selected.Count > 0;
        DeleteRecycleBinCommandButton.IsEnabled =
            interactionEnabled && selected.Count > 0;
        RefreshRecycleBinCommandButton.IsEnabled =
            interactionEnabled;
        RecycleBinViewModeButton.IsEnabled =
            interactionEnabled && hasItems;
        SelectAllRecycleBinCommandButton.IsEnabled =
            interactionEnabled && hasItems;
        EmptyRecycleBinCommandButton.IsEnabled =
            interactionEnabled && _allRecycleBinItems.Count > 0;

        if (!_isRecycleBinActive)
        {
            return;
        }

        SelectionStatusTextBlock.Text = selected.Count == 0
            ? string.Empty
            : $"Выбрано: {selected.Count}";
        SelectionStatusTextBlock.Visibility = selected.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private ListViewBase GetActiveRecycleBinView()
    {
        return _recycleBinGridMode
            ? RecycleBinGridView
            : RecycleBinListView;
    }

    private IReadOnlyList<RecycleBinListItem> GetSelectedRecycleBinItems()
    {
        if (!_isRecycleBinActive)
        {
            return [];
        }

        return GetActiveRecycleBinView()
            .SelectedItems
            .Cast<RecycleBinListItem>()
            .ToArray();
    }

    private void RestoreRecycleBinSelection(IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count == 0)
        {
            return;
        }

        var view = GetActiveRecycleBinView();
        foreach (var item in RecycleBinItems.Where(item =>
                     selectedIds.Contains(item.Id)))
        {
            view.SelectedItems.Add(item);
        }
    }

    private void ShowRecycleBinPreview(RecycleBinListItem item)
    {
        _selectedEntry = null;
        _selectedGame = null;
        _selectedRecycleBinItem = item;
        _currentSuggestion = null;
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        GameManagementPanel.Visibility = Visibility.Collapsed;
        RecycleBinManagementPanel.Visibility = Visibility.Visible;
        PreviewFileActions.Visibility = Visibility.Collapsed;
        OpenPreviewButton.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;
        PreviewGlyph.Glyph = item.Glyph;
        PreviewName.Text = item.Name;
        PreviewType.Text = item.DisplayType;
        PreviewPathLabel.Text = "Исходное расположение";
        PreviewPath.Text = item.OriginalPathDisplay;
        PreviewModifiedLabel.Text = "Удалён";
        PreviewModified.Text = item.DeletedDisplay;
        PreviewSizeLabel.Text = "Размер";
        PreviewSize.Text = item.SizeDisplay;
        ShowPreviewPanelIfSpaceAvailable();
    }

    private void ShowMultipleRecycleBinPreview(
        IReadOnlyList<RecycleBinListItem> items)
    {
        _selectedEntry = null;
        _selectedGame = null;
        _selectedRecycleBinItem = items[0];
        _currentSuggestion = null;
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        GameManagementPanel.Visibility = Visibility.Collapsed;
        RecycleBinManagementPanel.Visibility = Visibility.Visible;
        PreviewFileActions.Visibility = Visibility.Collapsed;
        OpenPreviewButton.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;
        PreviewGlyph.Glyph = "\uE8B3";
        PreviewName.Text = $"Выбрано: {items.Count}";
        PreviewType.Text =
            $"{items.Count(item => !item.IsDirectory)} файлов · " +
            $"{items.Count(item => item.IsDirectory)} папок";
        PreviewPathLabel.Text = "Исходные расположения";
        PreviewPath.Text = items
            .Select(item => item.OriginalFolderDisplay)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .Aggregate((current, path) => $"{current}\n{path}");
        PreviewModifiedLabel.Text = "Удалены";
        PreviewModified.Text = "Разное время";
        PreviewSizeLabel.Text = "Общий размер";
        PreviewSize.Text = FormatBytes(items.Sum(item => item.SizeBytes ?? 0));
        ShowPreviewPanelIfSpaceAvailable();
    }

    private static string FormatRecycleItemCount(int count)
    {
        var ending = count % 10 == 1 && count % 100 != 11
            ? "элемент"
            : count % 10 is >= 2 and <= 4
              && count % 100 is not (>= 12 and <= 14)
                ? "элемента"
                : "элементов";
        return $"{count} {ending}";
    }

    private void UpdateInterfaceState()
    {
        DashboardTabButton.Visibility = Visibility.Visible;
        WorkspaceTabsScroller.Visibility = _workspaceTabs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var dashboardTitle = _dashboardSession?.Current.Title ?? "Проекты";
        DashboardTabLabel.Text = GetTabLabel(dashboardTitle);
        DashboardTabIcon.Glyph = GetNavigationGlyph(
            dashboardTitle,
            "\uE8B7");

        HomeView.Visibility =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.IsHome
            && !_isWorkspaceTabActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        WorkspaceView.Visibility =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.IsHome
            && _isWorkspaceTabActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        GameLibraryView.Visibility =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.IsGames
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecycleBinView.Visibility =
            _isRecycleBinActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        SettingsView.Visibility =
            _isSettingsActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        BrowserView.Visibility =
            !_isRecycleBinActive
            && !_isSettingsActive
            && !ViewModel.IsHome
            && !ViewModel.IsGames
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateHomeDashboardVisibility();
        BackButton.IsEnabled =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.CanGoBack;
        ForwardButton.IsEnabled =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.CanGoForward;
        UpButton.IsEnabled =
            !_isRecycleBinActive
            && !_isSettingsActive
            && ViewModel.CanNavigateUp;
        RefreshButton.IsEnabled = _isRecycleBinActive
            ? !_isRecycleBinLoading && !_isRecycleBinOperationRunning
            : !_isSettingsActive
              && !ViewModel.IsBusy
              && !_fileOperationRunning;
        SearchBox.IsEnabled =
            !_isSettingsActive
            && (_isRecycleBinActive || ViewModel.CanSearch);
        SearchBox.PlaceholderText = _isRecycleBinActive
            ? "Поиск в Корзине"
            : _isSettingsActive
                ? "Поиск недоступен в настройках"
            : ViewModel.IsHome
                ? ViewModel.CanSearch
                    ? "Поиск в недавних файлах"
                    : "Поиск недоступен в AI-центре"
            : $"Поиск в {ViewModel.CurrentTitle}";
        ToolTipService.SetToolTip(
            SearchBox,
            _isRecycleBinActive
                ? "Поиск по имени, исходному расположению и типу"
                : _isSettingsActive
                    ? "Поиск недоступен в настройках"
                : ViewModel.IsHome
                    ? ViewModel.CanSearch
                        ? "Фильтр недавних файлов на главной странице"
                        : "Откройте AI-проекты, модели, Skills или MCP для поиска"
                    : "Введите запрос; Enter ищет во всех вложенных папках");
        AutomationProperties.SetName(
            SearchBox,
            _isRecycleBinActive
                ? "Поиск в Корзине"
                : _isSettingsActive
                    ? "Поиск недоступен в настройках"
                : $"Поиск в {ViewModel.CurrentTitle}");
        if (!ReferenceEquals(
                FocusManager.GetFocusedElement(RootGrid.XamlRoot),
                SearchBox)
            && !_isRecycleBinActive
            && !_isSettingsActive
            && !string.Equals(
                SearchBox.Text,
                ViewModel.SearchQuery,
                StringComparison.Ordinal))
        {
            SetSearchBoxText(ViewModel.SearchQuery);
        }

        if (!ReferenceEquals(
                FocusManager.GetFocusedElement(RootGrid.XamlRoot),
                AddressBox)
            && !string.Equals(
                AddressBox.Text,
                _isRecycleBinActive
                    ? "Корзина"
                    : _isSettingsActive
                        ? "Настройки Nexus"
                        : ViewModel.DisplayAddress,
                StringComparison.Ordinal))
        {
            AddressBox.Text = _isRecycleBinActive
                ? "Корзина"
                : _isSettingsActive
                    ? "Настройки Nexus"
                    : ViewModel.DisplayAddress;
        }
        AddressBox.IsEnabled = !_isRecycleBinActive && !_isSettingsActive;

        UpdateCollectionHeader();
        var showCollectionCommands =
            CollectionHeader.Visibility == Visibility.Visible;
        var browserCommandVisibility = showCollectionCommands
            ? Visibility.Collapsed
            : Visibility.Visible;
        var collectionCommandVisibility = showCollectionCommands
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (BrowserCommandBar.Visibility != browserCommandVisibility)
        {
            BrowserCommandBar.Visibility = browserCommandVisibility;
        }

        if (CollectionCommandBar.Visibility != collectionCommandVisibility)
        {
            CollectionCommandBar.Visibility = collectionCommandVisibility;
        }
        DashboardSortLabel.Text = ViewModel.SortLabel;
        SortCommandButton.Label = ViewModel.SortLabel;
        CollectionSortCommandButton.Label = ViewModel.SortLabel;
        UpdateSortHeaderIndicators();
        ViewModeCommandButton.Label = _isGridMode ? "Список" : "Плитки";
        CollectionViewModeCommandButton.Label =
            _isGridMode ? "Список" : "Плитки";

        LoadingIndicator.IsActive = ViewModel.IsBusy;
        LoadingIndicator.Visibility = ViewModel.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        GamesLoadingIndicator.IsActive = ViewModel.IsBusy && ViewModel.IsGames;
        GamesLoadingIndicator.Visibility =
            ViewModel.IsBusy && ViewModel.IsGames
                ? Visibility.Visible
                : Visibility.Collapsed;
        OperationProgressPanel.Visibility = _fileOperationRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        PauseFileOperationButton.Visibility = _operationPauseController is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PauseFileOperationButton.IsEnabled =
            _fileOperationRunning
            && _operationPauseController is not null
            && _operationCancellation is { IsCancellationRequested: false };
        CancelFileOperationButton.IsEnabled =
            _fileOperationRunning
            && _operationCancellation is { IsCancellationRequested: false };
        RestoreHiddenGamesButton.Visibility =
            ViewModel.IsGames && ViewModel.HiddenGamesCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        RestoreHiddenGamesLabel.Text = ViewModel.HiddenGamesCount == 1
            ? "Вернуть скрытую"
            : $"Вернуть скрытые ({ViewModel.HiddenGamesCount})";
        UpdateBrowserModeVisibility();

        var visibleError = _isSettingsActive
            ? _operationErrorMessage
            : _isRecycleBinActive
            ? _operationErrorMessage
            : ViewModel.ErrorMessage ?? _operationErrorMessage;
        ErrorInfoBar.Message = visibleError ?? string.Empty;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(visibleError);
        if (!_isRecycleBinActive && !_isSettingsActive)
        {
            SyncNavigationSelectionToLocation();
        }

        BrowserEmptyState.Visibility =
            !ViewModel.IsHome
            && !ViewModel.IsGames
            && !ViewModel.IsBusy
            && string.IsNullOrWhiteSpace(ViewModel.ErrorMessage)
            && ViewModel.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        GamesEmptyState.Visibility =
            ViewModel.IsGames
            && !ViewModel.IsBusy
            && string.IsNullOrWhiteSpace(ViewModel.ErrorMessage)
            && ViewModel.Games.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (ViewModel.IsGames
            && !string.IsNullOrWhiteSpace(ViewModel.SearchQuery))
        {
            GamesEmptyTitle.Text = "Игра не найдена";
            GamesEmptyDescription.Text =
                $"В библиотеке нет совпадений по запросу «{ViewModel.SearchQuery}».";
        }
        else
        {
            GamesEmptyTitle.Text = "Установленные игры не найдены";
            GamesEmptyDescription.Text =
                "Nexus проверил Steam, Epic Games, Xbox, торренты и добавленные папки. Добавьте каталог игры, если она установлена в нестандартном месте.";
        }
        GamesEmptyActionButton.Content =
            !string.IsNullOrWhiteSpace(ViewModel.SearchQuery)
                ? "Очистить поиск"
                : "Добавить папку игры";
        AutomationProperties.SetName(
            GamesEmptyActionButton,
            GamesEmptyActionButton.Content?.ToString() ?? "Действие");

        if (!string.IsNullOrWhiteSpace(ViewModel.SearchQuery))
        {
            BrowserEmptyTitle.Text = "Ничего не найдено";
            BrowserEmptyDescription.Text =
                $"По запросу «{ViewModel.SearchQuery}» в текущем разделе нет совпадений.";
        }
        else if (ViewModel.IsFavorites)
        {
            BrowserEmptyTitle.Text = "Избранных файлов пока нет";
            BrowserEmptyDescription.Text =
                "Выберите файл или папку и используйте команду «Добавить в избранное».";
        }
        else
        {
            BrowserEmptyTitle.Text = "Здесь пока пусто";
            BrowserEmptyDescription.Text =
                ViewModel.CurrentKind == NavigationKind.Recent
                    ? "Недавние файлы в Документах, Загрузках и на рабочем столе не найдены."
                    : ViewModel.CurrentKind == NavigationKind.Torrents
                        ? "Файлы .torrent не найдены в Загрузках, на Рабочем столе, фиксированных дисках и в добавленных игровых папках."
                    : ViewModel.CurrentKind == NavigationKind.Applications
                        ? "Ярлыки приложений в меню «Пуск» не найдены."
                    : ViewModel.CurrentKind == NavigationKind.Archive
                        ? "Архив — обычная папка Windows. Переместите сюда завершённые материалы или создайте подпапку."
                    : ViewModel.CurrentKind == NavigationKind.AiProjects
                        ? GetAiProjectsEmptyDescription()
                    : ViewModel.CurrentKind is NavigationKind.OptionalFolder
                        or NavigationKind.Collection
                        ? "Источник пока не настроен или не содержит элементов."
                    : "В этом разделе пока нет файлов и папок.";
        }

        BrowserEmptyActionButton.Content =
            !string.IsNullOrWhiteSpace(ViewModel.SearchQuery)
                ? "Очистить поиск"
                : ViewModel.CurrentKind == NavigationKind.Favorites
                    ? "Открыть проекты"
                    : ViewModel.CurrentKind == NavigationKind.Recent
                        ? "Открыть документы"
                    : ViewModel.CurrentKind == NavigationKind.Torrents
                        ? "Открыть загрузки"
                        : ViewModel.CurrentKind == NavigationKind.Applications
                            ? "Управление приложениями"
                            : ViewModel.CurrentKind == NavigationKind.Collection
                                ? "Открыть папку конфигурации"
                            : ViewModel.CurrentKind == NavigationKind.OptionalFolder
                                ? Directory.Exists(ViewModel.CurrentPath)
                                    ? "Создать папку"
                                    : "Создать расположение"
                            : CanModifyCurrentFolder()
                                ? "Создать папку"
                                : "Проверить снова";
        AutomationProperties.SetName(
            BrowserEmptyActionButton,
            BrowserEmptyActionButton.Content?.ToString() ?? "Действие");

        UpdateCommandAvailability();
        UpdateRecycleBinUiState();
        UpdateRecentEmptyStates();
        SetTabVisuals();
    }

    private string GetAiProjectsEmptyDescription()
    {
        var diagnostics = _aiWorkspaceService.GetLastDiscoveryDiagnostics();
        if (diagnostics is null)
        {
            return "Поиск AI-проектов ещё не завершён. Нажмите «Обновить» или добавьте папку поиска.";
        }

        if (diagnostics.SearchRootCount == 0)
        {
            return "Доступные папки для поиска AI-проектов не найдены.";
        }

        var truncated = diagnostics.IsTruncated
            ? " Часть дерева была пропущена по безопасному лимиту — добавьте нужную папку как отдельный источник."
            : string.Empty;
        return $"Nexus проверил {FormatPathCount(diagnostics.SearchRootCount)}, но не нашёл проектов с подтверждёнными AI-признаками.{truncated}";
    }

    private string GetNavigationGlyph(string label, string fallback)
    {
        if (string.Equals(
                label,
                "Корзина",
                StringComparison.CurrentCultureIgnoreCase))
        {
            return "\uE74D";
        }

        return ViewModel.NavigationTargets
            .FirstOrDefault(target => string.Equals(
                target.Label,
                label,
                StringComparison.CurrentCultureIgnoreCase))
            ?.Glyph
            ?? fallback;
    }

    private void UpdateCollectionHeader()
    {
        var isApplications =
            ViewModel.CurrentKind == NavigationKind.Applications;
        var isTorrents = ViewModel.CurrentKind == NavigationKind.Torrents;
        var isRecent = ViewModel.CurrentKind == NavigationKind.Recent;
        var isAiProjects = ViewModel.CurrentKind == NavigationKind.AiProjects;
        var isMcp = ViewModel.CurrentKind == NavigationKind.Collection
            && string.Equals(
                ViewModel.CurrentTitle,
                "MCP",
                StringComparison.CurrentCultureIgnoreCase);
        var isFavorites = ViewModel.IsFavorites;
        var showHeader = isApplications
            || isTorrents
            || isRecent
            || isAiProjects
            || isMcp
            || isFavorites;

        CollectionHeader.Visibility = showHeader
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!showHeader)
        {
            return;
        }

        CollectionTitle.Text = ViewModel.CurrentTitle;
        CollectionAddAiRootButton.Visibility = isAiProjects
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionDescription.Text = isApplications
            ? ViewModel.ShowSystemApplications
                ? "Установленные программы и системные приложения Windows"
                : ViewModel.HiddenSystemApplicationsCount > 0
                    ? $"Установленные программы · системные скрыты ({ViewModel.HiddenSystemApplicationsCount})"
                    : "Установленные программы · системные приложения скрыты"
            : isTorrents
                ? "Торрент-файлы из пользовательских и игровых источников"
                : isAiProjects
                    ? "Реальные локальные рабочие пространства с AI-зависимостями, моделями или агентскими конфигурациями"
                : isMcp
                    ? "Конфигурации MCP из Codex, Claude, Cursor, VS Code и найденных AI-проектов"
                : isFavorites
                    ? "Закреплённые файлы и папки в одном месте"
                    : "Недавно изменённые документы и файлы";
        CollectionCount.Text = ViewModel.Items.Count switch
        {
            1 => "1 элемент",
            var count when count % 10 is >= 2 and <= 4
                && count % 100 is not (>= 12 and <= 14)
                => $"{count} элемента",
            var count => $"{count} элементов"
        };
    }

    private void SyncNavigationSelectionToLocation()
    {
        string? targetId = ViewModel.CurrentKind switch
        {
            NavigationKind.Home => ViewModel.CurrentTitle switch
            {
                "Главная" => "home",
                "AI-центр" => "ai-center",
                _ => "projects"
            },
            NavigationKind.Games => "games",
            NavigationKind.Favorites => "favorites",
            NavigationKind.Applications => "applications",
            NavigationKind.Torrents => "torrents",
            NavigationKind.Recent => "recent",
            NavigationKind.AiProjects => "ai-projects",
            NavigationKind.Computer => "computer",
            NavigationKind.Network => "network",
            NavigationKind.Media => "media",
            NavigationKind.Collection when string.Equals(
                ViewModel.CurrentTitle,
                "MCP",
                StringComparison.CurrentCultureIgnoreCase) => "mcp",
            _ => null
        };

        if (targetId is null
            && ViewModel.CurrentKind is NavigationKind.Folder
                or NavigationKind.OptionalFolder
                or NavigationKind.Archive
            && !string.IsNullOrWhiteSpace(ViewModel.CurrentPath))
        {
            targetId = ViewModel.NavigationTargets
                .Where(target =>
                    !string.IsNullOrWhiteSpace(target.Path)
                    && IsSameOrAncestorPath(target.Path!, ViewModel.CurrentPath))
                .OrderByDescending(target => target.Path!.Length)
                .Select(target => target.Id)
                .FirstOrDefault();
        }

        if (targetId is null)
        {
            targetId = ViewModel.NavigationTargets
                .Where(target =>
                    target.Kind == ViewModel.CurrentKind
                    &&
                    string.Equals(
                        target.Label,
                        ViewModel.CurrentTitle,
                        StringComparison.CurrentCultureIgnoreCase))
                .Select(target => target.Id)
                .FirstOrDefault();
        }

        if (targetId is null)
        {
            ClearNavigationSelection();
            return;
        }

        SetNavigationSelectionById(targetId);
    }

    private static bool IsSameOrAncestorPath(string candidateParent, string path)
    {
        try
        {
            var parent = Path.GetFullPath(candidateParent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var child = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
                || child.StartsWith(
                    parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private async Task CreateWorkspaceTabAsync()
    {
        CaptureActiveSession();
        var initialState = ViewModel.CaptureSession();
        if (initialState is null)
        {
            return;
        }

        var tab = new WorkspaceTabState(initialState)
        {
            GridMode = _isGridMode
        };
        _workspaceTabs.Add(tab);
        _activeWorkspaceTab = tab;
        _isHomeTabActive = false;
        _isWorkspaceTabActive = true;
        SetNavigationSelection(ProjectsNavigationButton);
        RefreshWorkspaceTabStrip();

        await ViewModel.NavigateToTargetAsync("projects");
        CaptureActiveSession();
        ClosePreview();
        UpdateInterfaceState();
    }

    private async void DynamicWorkspaceTabButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkspaceTabState tab }
            || ReferenceEquals(tab, _activeWorkspaceTab)
            && _isWorkspaceTabActive)
        {
            return;
        }

        await ActivateWorkspaceTabAsync(tab);
    }

    private async Task ActivateWorkspaceTabAsync(WorkspaceTabState tab)
    {
        CaptureActiveSession();
        var targetGridMode = tab.GridMode;
        _activeWorkspaceTab = tab;
        _isHomeTabActive = false;
        _isWorkspaceTabActive = true;
        if (!await ViewModel.RestoreSessionAsync(tab.Session))
        {
            await ViewModel.NavigateToTargetAsync("computer");
            ShowOperationError(
                $"Расположение вкладки «{tab.Label}» недоступно. Открыт раздел «Этот компьютер».");
            var fallbackSession = ViewModel.CaptureSession();
            if (fallbackSession is not null)
            {
                tab.Session = fallbackSession;
                tab.Label = GetTabLabel(fallbackSession.Current.Title);
                tab.Glyph = GetNavigationGlyph(
                    fallbackSession.Current.Title,
                    "\uE7F8");
            }
        }

        SetViewMode(targetGridMode);
        RestoreWorkspaceTabSelection(tab);
        SyncNavigationSelectionToLocation();
        ClosePreview();
        UpdateInterfaceState();
    }

    private async void DynamicWorkspaceTabCloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceTabState tab })
        {
            await CloseWorkspaceTabAsync(tab);
        }
    }

    private Task CloseActiveWorkspaceTabAsync()
    {
        return _activeWorkspaceTab is null
            ? Task.CompletedTask
            : CloseWorkspaceTabAsync(_activeWorkspaceTab);
    }

    private async Task CloseWorkspaceTabAsync(WorkspaceTabState tab)
    {
        if (!_workspaceTabs.Contains(tab))
        {
            return;
        }

        var wasVisible = ReferenceEquals(tab, _activeWorkspaceTab)
            && _isWorkspaceTabActive;
        if (wasVisible)
        {
            CaptureActiveSession();
        }

        var removedIndex = _workspaceTabs.IndexOf(tab);
        _workspaceTabs.Remove(tab);
        if (!wasVisible)
        {
            if (ReferenceEquals(tab, _activeWorkspaceTab))
            {
                _activeWorkspaceTab = _workspaceTabs.Count == 0
                    ? null
                    : _workspaceTabs[
                        Math.Clamp(
                            removedIndex,
                            0,
                            _workspaceTabs.Count - 1)];
            }

            RefreshWorkspaceTabStrip();
            return;
        }

        BrowserListView.SelectedItems.Clear();
        BrowserGridView.SelectedItems.Clear();
        ClosePreview();
        if (_workspaceTabs.Count > 0)
        {
            _activeWorkspaceTab =
                _workspaceTabs[Math.Clamp(removedIndex, 0, _workspaceTabs.Count - 1)];
            var targetGridMode = _activeWorkspaceTab.GridMode;
            _isHomeTabActive = false;
            _isWorkspaceTabActive = true;
            if (!await ViewModel.RestoreSessionAsync(_activeWorkspaceTab.Session))
            {
                await ViewModel.NavigateToTargetAsync("computer");
                ShowOperationError(
                    $"Расположение вкладки «{_activeWorkspaceTab.Label}» недоступно. Открыт раздел «Этот компьютер».");
                var fallbackSession = ViewModel.CaptureSession();
                if (fallbackSession is not null)
                {
                    _activeWorkspaceTab.Session = fallbackSession;
                    _activeWorkspaceTab.Label =
                        GetTabLabel(fallbackSession.Current.Title);
                    _activeWorkspaceTab.Glyph = GetNavigationGlyph(
                        fallbackSession.Current.Title,
                        "\uE7F8");
                }
            }

            SetViewMode(targetGridMode);
            RestoreWorkspaceTabSelection(_activeWorkspaceTab);
            SyncNavigationSelectionToLocation();
            ClosePreview();
        }
        else
        {
            _activeWorkspaceTab = null;
            _isHomeTabActive = false;
            _isWorkspaceTabActive = false;
            if (_dashboardSession is not null)
            {
                if (!await ViewModel.RestoreSessionAsync(_dashboardSession))
                {
                    await ViewModel.NavigateToTargetAsync("projects");
                    _dashboardSession = ViewModel.CaptureSession();
                    ShowOperationError(
                        "Предыдущее расположение вкладки недоступно. Открыты Проекты.");
                }
            }
            else
            {
                await ViewModel.NavigateToTargetAsync("projects");
                _dashboardSession = ViewModel.CaptureSession();
            }

            ClosePreview();
        }

        UpdateInterfaceState();
    }

    private void CaptureActiveSession()
    {
        if (_isRecycleBinActive || _isSettingsActive)
        {
            return;
        }

        var state = ViewModel.CaptureSession();
        if (state is null)
        {
            return;
        }

        if (_isWorkspaceTabActive)
        {
            if (_activeWorkspaceTab is null)
            {
                _activeWorkspaceTab = new WorkspaceTabState(state)
                {
                    GridMode = _isGridMode
                };
                _workspaceTabs.Add(_activeWorkspaceTab);
            }
            else
            {
                _activeWorkspaceTab.Session = state;
                _activeWorkspaceTab.Label = GetTabLabel(state.Current.Title);
                _activeWorkspaceTab.Glyph = GetNavigationGlyph(
                    state.Current.Title,
                    state.Current.Kind == NavigationKind.Games
                        ? "\uE7FC"
                        : "\uE8A5");
                _activeWorkspaceTab.SelectedPaths = GetSelectedEntries()
                    .Select(entry => entry.FullPath)
                    .ToArray();
            }
        }
        else if (_isHomeTabActive)
        {
            _homeSession = state;
        }
        else
        {
            _dashboardSession = state;
        }
    }

    private void RestoreWorkspaceTabSelection(WorkspaceTabState tab)
    {
        if (tab.SelectedPaths.Count == 0
            || BrowserView.Visibility != Visibility.Visible)
        {
            return;
        }

        var selectedPaths = tab.SelectedPaths.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var target = _isGridMode
            ? (ListViewBase)BrowserGridView
            : BrowserListView;
        target.SelectedItems.Clear();
        foreach (var entry in ViewModel.Items.Where(item =>
                     selectedPaths.Contains(item.FullPath)))
        {
            target.SelectedItems.Add(entry);
        }

        if (target.SelectedItems.Count > 0)
        {
            target.ScrollIntoView(target.SelectedItems[0]);
        }
    }

    private static string GetTabLabel(string? title)
    {
        const int maximumLength = 24;
        var label = string.IsNullOrWhiteSpace(title) ? "Файлы" : title.Trim();
        return label.Length <= maximumLength
            ? label
            : $"{label[..(maximumLength - 1)]}…";
    }

    private void RefreshWorkspaceTabStrip()
    {
        var signature =
            $"{_isWorkspaceTabActive}:{_activeWorkspaceTab?.Id}:" +
            string.Join(
                "|",
                _workspaceTabs.Select(tab =>
                    $"{tab.Id}:{tab.Label}:{tab.Glyph}:{tab.Session.Current.Path}"));
        if (string.Equals(
                signature,
                _workspaceTabVisualSignature,
                StringComparison.Ordinal))
        {
            WorkspaceTabsScroller.Visibility = _workspaceTabs.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        _workspaceTabVisualSignature = signature;
        WorkspaceTabStrip.Children.Clear();
        var activeBrush =
            (Brush)Application.Current.Resources["NexusRaisedBrush"];
        var transparentBrush = new SolidColorBrush(Colors.Transparent);
        var accentBrush =
            (Brush)Application.Current.Resources["NexusAccentBrush"];
        var tertiaryBrush =
            (Brush)Application.Current.Resources["NexusTextTertiaryBrush"];

        foreach (var tab in _workspaceTabs)
        {
            var host = new Grid
            {
                Width = 190,
                Height = 46
            };
            var activateButton = new Button
            {
                Tag = tab,
                Padding = new Thickness(14, 0, 40, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = _isWorkspaceTabActive
                    && ReferenceEquals(tab, _activeWorkspaceTab)
                        ? activeBrush
                        : transparentBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12, 12, 0, 0)
            };
            AutomationProperties.SetName(
                activateButton,
                $"Вкладка {tab.Label}");
            AutomationProperties.SetItemStatus(
                activateButton,
                _isWorkspaceTabActive
                    && ReferenceEquals(tab, _activeWorkspaceTab)
                        ? "Активная вкладка"
                        : "Вкладка");
            ToolTipService.SetToolTip(activateButton, tab.Session.Current.Path
                ?? tab.Label);
            activateButton.Click += DynamicWorkspaceTabButton_Click;
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };
            content.Children.Add(new FontIcon
            {
                Glyph = tab.Glyph,
                FontSize = 16,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = tab.Label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            activateButton.Content = content;
            host.Children.Add(activateButton);

            var closeButton = new Button
            {
                Tag = tab,
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = transparentBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 11,
                    Foreground = tertiaryBrush
                }
            };
            AutomationProperties.SetName(
                closeButton,
                $"Закрыть вкладку {tab.Label}");
            ToolTipService.SetToolTip(closeButton, "Закрыть вкладку (Ctrl+W)");
            closeButton.Click += DynamicWorkspaceTabCloseButton_Click;
            host.Children.Add(closeButton);
            WorkspaceTabStrip.Children.Add(host);
        }

        WorkspaceTabsScroller.Visibility = _workspaceTabs.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetTabVisuals()
    {
        var activeBrush = (Brush)Application.Current.Resources["NexusRaisedBrush"];
        var transparentBrush = new SolidColorBrush(Colors.Transparent);

        HomeTabButton.Background =
            _isHomeTabActive ? activeBrush : transparentBrush;
        DashboardTabButton.Background =
            !_isHomeTabActive && !_isWorkspaceTabActive
                ? activeBrush
                : transparentBrush;
        RefreshWorkspaceTabStrip();
    }

    private void UpdateRecentEmptyStates()
    {
        var emptyVisibility = ViewModel.RecentItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        RecentEmptyState.Visibility = emptyVisibility;
        WorkspaceRecentEmptyState.Visibility = emptyVisibility;
    }

    private void SetViewMode(bool gridMode)
    {
        if (_isWorkspaceTabActive && _activeWorkspaceTab is not null)
        {
            _activeWorkspaceTab.GridMode = gridMode;
        }

        if (_isGridMode == gridMode)
        {
            UpdateBrowserModeVisibility();
            UpdateCommandAvailability();
            return;
        }

        var selectedPaths = GetSelectedEntries()
            .Select(entry => entry.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _isGridMode = gridMode;
        UpdateBrowserModeVisibility();

        if (selectedPaths.Count > 0)
        {
            var target = gridMode
                ? (ListViewBase)BrowserGridView
                : BrowserListView;
            target.SelectedItems.Clear();
            foreach (var entry in ViewModel.Items.Where(item =>
                         selectedPaths.Contains(item.FullPath)))
            {
                target.SelectedItems.Add(entry);
            }

            if (target.SelectedItems.Count > 0)
            {
                target.ScrollIntoView(target.SelectedItems[0]);
            }
        }

        UpdateCommandAvailability();
    }

    private void UpdateBrowserModeVisibility()
    {
        var showItems =
            BrowserView.Visibility == Visibility.Visible
            && !ViewModel.IsBusy;
        BrowserListView.Visibility =
            showItems && !_isGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        ListColumnHeader.Visibility =
            showItems && !_isGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        BrowserGridView.Visibility =
            showItems && _isGridMode
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private IReadOnlyList<FileSystemEntry> GetSelectedEntries()
    {
        if (BrowserView.Visibility != Visibility.Visible)
        {
            return [];
        }

        return (_isGridMode
                ? BrowserGridView.SelectedItems
                : BrowserListView.SelectedItems)
            .Cast<FileSystemEntry>()
            .ToArray();
    }

    private void ClearBrowserSelection()
    {
        BrowserListView.SelectedItems.Clear();
        BrowserGridView.SelectedItems.Clear();
        ClosePreview();
        UpdateCommandAvailability();
    }

    private void SelectAllItems()
    {
        if (BrowserView.Visibility != Visibility.Visible)
        {
            return;
        }

        var view = _isGridMode
            ? (ListViewBase)BrowserGridView
            : BrowserListView;
        view.SelectAll();
    }

    private void UpdateCommandAvailability()
    {
        if (NewFolderCommandButton is null)
        {
            return;
        }

        var selectedEntries = GetSelectedEntries();
        var selectedCount = selectedEntries.Count;
        SelectionStatusTextBlock.Text = selectedCount switch
        {
            0 => string.Empty,
            1 => "Выбран 1 элемент",
            >= 2 and <= 4 => $"Выбрано {selectedCount} элемента",
            _ => $"Выбрано {selectedCount} элементов"
        };
        SelectionStatusTextBlock.Visibility = selectedCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        var singleCapabilities = selectedCount == 1
            ? GetEntryCapabilities(selectedEntries[0])
            : default;
        var canModifyFolder = CanModifyCurrentFolder();
        OpenSelectionCommandButton.IsEnabled =
            selectedCount == 1 && singleCapabilities.CanOpen;
        CollectionPropertiesCommandButton.IsEnabled =
            selectedCount > 0
            && selectedEntries.All(entry => GetEntryCapabilities(entry).CanShowProperties);
        CollectionSelectAllCommandButton.IsEnabled =
            BrowserView.Visibility == Visibility.Visible
            && ViewModel.Items.Count > 0;

        NewFolderCommandButton.IsEnabled = canModifyFolder;
        PasteCommandButton.IsEnabled =
            canModifyFolder
            && (_clipboardPaths.Count > 0 || HasSystemFileClipboard());
        CopyCommandButton.IsEnabled =
            selectedCount > 0
            && selectedEntries.All(entry => GetEntryCapabilities(entry).CanCopy);
        CutCommandButton.IsEnabled =
            selectedCount > 0
            && selectedEntries.All(entry => GetEntryCapabilities(entry).CanCut);
        RenameCommandButton.IsEnabled =
            selectedCount == 1 && singleCapabilities.CanRename;
        DeleteCommandButton.IsEnabled =
            selectedCount > 0
            && selectedEntries.All(entry => GetEntryCapabilities(entry).CanDelete);
        PropertiesCommandButton.IsEnabled =
            selectedCount > 0
            && selectedEntries.All(entry => GetEntryCapabilities(entry).CanShowProperties);
        FavoriteCommandButton.IsEnabled =
            selectedCount == 1 && singleCapabilities.CanFavorite;
        if (selectedCount == 1)
        {
            var selected = GetSelectedEntries()[0];
            var favorite = ViewModel.IsFavorite(selected.FullPath);
            FavoriteCommandButton.Label = favorite
                ? "Удалить из избранного"
                : "Добавить в избранное";
            FavoriteCommandButton.Icon = new FontIcon
            {
                Glyph = favorite ? "\uE735" : "\uE734"
            };
        }
        else
        {
            FavoriteCommandButton.Label = "Добавить в избранное";
            FavoriteCommandButton.Icon = new FontIcon { Glyph = "\uE734" };
        }
        SelectAllCommandButton.IsEnabled =
            BrowserView.Visibility == Visibility.Visible && ViewModel.Items.Count > 0;
    }

    private bool CanModifyCurrentFolder()
    {
        return BrowserView.Visibility == Visibility.Visible
            && !ViewModel.IsBusy
            && !_fileOperationRunning
            && Directory.Exists(ViewModel.CurrentPath);
    }

    private EntryCapabilities GetEntryCapabilities(FileSystemEntry entry)
    {
        var isApplicationCollection =
            ViewModel.CurrentKind == NavigationKind.Applications;
        var isVirtualApplication = IsVirtualApplicationEntry(entry);
        var exists = entry.IsDirectory
            ? Directory.Exists(entry.FullPath)
            : File.Exists(entry.FullPath) || isVirtualApplication;
        if (!exists)
        {
            return default;
        }

        var isRoot = entry.IsDirectory && IsRootPath(entry.FullPath);
        var canMutate = !ViewModel.IsBusy
            && !_fileOperationRunning
            && !isApplicationCollection
            && !isRoot;

        return new EntryCapabilities(
            CanOpen: true,
            CanCopy: !isVirtualApplication,
            CanCut: canMutate,
            CanRename: canMutate,
            CanDelete: canMutate,
            CanFavorite: !isApplicationCollection,
            CanShowProperties: !isVirtualApplication);
    }

    private bool IsVirtualApplicationEntry(FileSystemEntry entry) =>
        ViewModel.CurrentKind == NavigationKind.Applications
        && entry.FullPath.StartsWith(
            "shell:AppsFolder\\",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsRootPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return true;
        }
    }

    private void UpdateHomeDashboardVisibility()
    {
        var isFocusedAiCenter = ViewModel.IsHome
            && string.Equals(
                ViewModel.CurrentTitle,
                "AI-центр",
                StringComparison.CurrentCultureIgnoreCase);

        HomeDashboardActions.Visibility = isFocusedAiCenter
            ? Visibility.Collapsed
            : Visibility.Visible;
        HomeSpacesGrid.Visibility = isFocusedAiCenter
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecentFilesHeader.Visibility = isFocusedAiCenter
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecentFilesPanel.Visibility = isFocusedAiCenter
            ? Visibility.Collapsed
            : Visibility.Visible;
        AiCenterSectionTitle.Text = isFocusedAiCenter
            ? "Локальные источники"
            : "AI-центр";
        AiCenterSectionSubtitle.Text = isFocusedAiCenter
            ? "Реальные проекты, модели и конфигурации, найденные на этом компьютере"
            : "Локальные проекты и конфигурации, найденные на этом компьютере";
        AiCenterSectionHeader.Margin = isFocusedAiCenter
            ? new Thickness(0, 16, 0, 12)
            : new Thickness(0, 20, 0, 12);
    }

    private void SetDashboardViewMode(bool gridMode)
    {
        _dashboardGridMode = gridMode;
        var selectedBrush =
            (Brush)Application.Current.Resources["NexusSelectedBrush"];
        var transparentBrush = new SolidColorBrush(Colors.Transparent);
        ListModeButton.Background = gridMode ? transparentBrush : selectedBrush;
        GridModeButton.Background = gridMode ? selectedBrush : transparentBrush;
        WorkspaceListModeButton.Background =
            gridMode ? transparentBrush : selectedBrush;
        WorkspaceGridModeButton.Background =
            gridMode ? selectedBrush : transparentBrush;
        UpdateDashboardSpaceLayout();
    }

    private void UpdateDashboardSpaceLayout()
    {
        var useTwoColumns = !_dashboardGridMode || RootGrid.ActualWidth < 1080;
        ArrangeSpaceButtons(
            HomeSpacesGrid,
            [
                HomeDocumentsSpaceButton,
                HomeWebAppsSpaceButton,
                HomeResourcesSpaceButton,
                HomeDownloadsSpaceButton
            ],
            useTwoColumns);
        ArrangeSpaceButtons(
            WorkspaceSpacesGrid,
            [
                WorkspaceDocumentsSpaceButton,
                WorkspaceWebAppsSpaceButton,
                WorkspaceResourcesSpaceButton,
                WorkspaceDownloadsSpaceButton
            ],
            useTwoColumns);

        var aiColumnCount = RootGrid.ActualWidth < 900
            ? 1
            : RootGrid.ActualWidth < 1800
                ? 2
                : 4;
        for (var index = 0; index < AiCenterGrid.ColumnDefinitions.Count; index++)
        {
            AiCenterGrid.ColumnDefinitions[index].Width =
                index < aiColumnCount
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
        }

        for (var index = 0; index < AiCenterGrid.Children.Count; index++)
        {
            if (AiCenterGrid.Children[index] is FrameworkElement card)
            {
                Grid.SetColumn(card, index % aiColumnCount);
                Grid.SetRow(card, index / aiColumnCount);
            }
        }
    }

    private static void ArrangeSpaceButtons(
        Grid grid,
        IReadOnlyList<Button> buttons,
        bool useTwoColumns)
    {
        for (var index = 0; index < grid.ColumnDefinitions.Count; index++)
        {
            grid.ColumnDefinitions[index].Width =
                index < 2 || !useTwoColumns
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
        }

        for (var index = 0; index < buttons.Count; index++)
        {
            Grid.SetColumn(buttons[index], useTwoColumns ? index % 2 : index);
            Grid.SetRow(buttons[index], useTwoColumns ? index / 2 : 0);
        }
    }

    private void ShowMultipleSelectionPreview(
        IReadOnlyList<FileSystemEntry> entries)
    {
        _selectedEntry = entries[0];
        GameManagementPanel.Visibility = Visibility.Collapsed;
        PreviewModifiedLabel.Text = "Изменён";
        PreviewSizeLabel.Text = "Размер";
        _currentSuggestion = null;
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        AutomationProperties.SetName(
            PreviewImage,
            $"Предпросмотр выбранных элементов: {entries.Count}");
        PreviewImage.MaxHeight = 210;
        PreviewImage.MaxWidth = double.PositiveInfinity;
        PreviewImage.Stretch = Stretch.Uniform;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;
        PreviewGlyph.Glyph = "\uE8B3";
        PreviewName.Text = $"Выбрано: {entries.Count}";
        PreviewType.Text =
            $"{entries.Count(item => !item.IsDirectory)} файлов · " +
            $"{entries.Count(item => item.IsDirectory)} папок";
        PreviewPath.Text = ViewModel.CurrentPath;
        PreviewModified.Text = "—";
        PreviewSize.Text = FormatBytes(entries.Sum(item => item.SizeBytes ?? 0));
        ShowPreviewPanelIfSpaceAvailable();
        OpenPreviewButton.IsEnabled = false;
        PreviewOpenInExplorerButton.IsEnabled = false;
    }

    private static string GetAvailableFolderName(string parentDirectory)
    {
        const string baseName = "Новая папка";
        var candidate = baseName;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(parentDirectory, candidate))
               || File.Exists(Path.Combine(parentDirectory, candidate)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox;
    }

    private void ShowPreview(FileSystemEntry entry)
    {
        _selectedGame = null;
        GameManagementPanel.Visibility = Visibility.Collapsed;
        PreviewModifiedLabel.Text = "Изменён";
        PreviewSizeLabel.Text = "Размер";
        PreviewName.Text = entry.Name;
        PreviewType.Text = entry.DisplayType;
        PreviewPath.Text = entry.FullPath;
        PreviewModified.Text = entry.DisplayModified;
        PreviewSize.Text = string.IsNullOrWhiteSpace(entry.DisplaySize) ? "—" : entry.DisplaySize;
        PreviewGlyph.Glyph = entry.IconGlyph;

        PreviewImage.Source = null;
        AutomationProperties.SetName(
            PreviewImage,
            $"Предпросмотр {entry.Name}");
        PreviewImage.MaxHeight = 210;
        PreviewImage.MaxWidth = double.PositiveInfinity;
        PreviewImage.Stretch = Stretch.Uniform;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;

        if (!entry.IsDirectory && IsImage(entry.FullPath))
        {
            PreviewGlyphShell.Visibility = Visibility.Visible;
            _ = LoadFilePreviewThumbnailAsync(entry);
        }
        else if (!entry.IsDirectory)
        {
            var title = Path.GetFileNameWithoutExtension(entry.Name)
                .Replace('_', ' ')
                .Trim();

            PreviewCoverTitle.Text = string.IsNullOrWhiteSpace(title)
                ? entry.Name
                : title;
            PreviewCoverBadge.Text = GetPreviewBadge(entry.FullPath);
            PreviewDocumentCover.Visibility = Visibility.Visible;
            PreviewGlyphShell.Visibility = Visibility.Collapsed;
            _ = LoadFilePreviewThumbnailAsync(entry);
        }

        ShowPreviewPanelIfSpaceAvailable();
        OpenPreviewButton.IsEnabled = true;
        OpenPreviewButton.Content = "Открыть";
        PreviewOpenInExplorerButton.IsEnabled =
            !IsVirtualApplicationEntry(entry);
        UpdateOrganizationSuggestion(entry);
    }

    private async Task LoadFilePreviewThumbnailAsync(FileSystemEntry entry)
    {
        var source = await _thumbnailService.GetAsync(entry.FullPath, 320);
        if (source is null
            || _selectedEntry is null
            || !string.Equals(
                _selectedEntry.FullPath,
                entry.FullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PreviewImage.Source = source;
        PreviewImage.Stretch = Stretch.Uniform;
        PreviewImage.MaxHeight = IsImage(entry.FullPath) ? 320 : 150;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Collapsed;
    }

    private async Task LoadGamePreviewArtworkAsync(GameEntry game)
    {
        var path = GetGameArtworkSourcePath(game);
        if (path is null)
        {
            return;
        }

        var isCachedShellIcon = path.Contains(
            $"{Path.DirectorySeparatorChar}IconCache{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        var source = await _thumbnailService.GetAsync(
            path,
            isCachedShellIcon ? 160u : 360u);
        if (source is null
            || !ReferenceEquals(_selectedGame, game))
        {
            return;
        }

        PreviewImage.Source = source;
        PreviewImage.Stretch = Stretch.Uniform;
        PreviewImage.MaxHeight = isCachedShellIcon ? 112 : 230;
        PreviewImage.MaxWidth = isCachedShellIcon
            ? 112
            : double.PositiveInfinity;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Collapsed;
    }

    private void ThumbnailImage_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        var state = _thumbnailImageStates.GetValue(
            image,
            static _ => new ThumbnailImageState());
        if (!state.IsTrackingTag)
        {
            state.TagCallbackToken = image.RegisterPropertyChangedCallback(
                FrameworkElement.TagProperty,
                ThumbnailImage_TagChanged);
            state.IsTrackingTag = true;
        }

        StartThumbnailLoad(image, state);
    }

    private void ThumbnailImage_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Image image
            || !_thumbnailImageStates.TryGetValue(image, out var state))
        {
            return;
        }

        if (state.IsTrackingTag)
        {
            image.UnregisterPropertyChangedCallback(
                FrameworkElement.TagProperty,
                state.TagCallbackToken);
            state.IsTrackingTag = false;
        }

        CancelThumbnailLoad(state);
        state.ExpectedItem = null;
        state.Version++;
        SetThumbnailVisualState(image, null);
    }

    private void ThumbnailImage_TagChanged(
        DependencyObject sender,
        DependencyProperty property)
    {
        if (sender is not Image image
            || image.XamlRoot is null)
        {
            return;
        }

        var state = _thumbnailImageStates.GetValue(
            image,
            static _ => new ThumbnailImageState());
        StartThumbnailLoad(image, state);
    }

    private void ThumbnailImage_ImageFailed(
        object sender,
        ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            SetThumbnailVisualState(image, null);
        }
    }

    private void StartThumbnailLoad(
        Image image,
        ThumbnailImageState state)
    {
        CancelThumbnailLoad(state);
        state.Version++;
        state.ExpectedItem = image.Tag;
        var version = state.Version;
        SetThumbnailVisualState(image, null);

        string? path;
        uint size;
        string accessibleName;
        switch (state.ExpectedItem)
        {
            case GameEntry game:
                path = GetGameArtworkSourcePath(game);
                size = IsCachedShellIcon(path) ? 128u : 256u;
                accessibleName = $"Обложка игры {game.Name}";
                break;
            case FileSystemEntry entry:
                path = string.IsNullOrWhiteSpace(entry.ThumbnailPath)
                    ? null
                    : entry.ThumbnailPath;
                size = 160u;
                accessibleName = $"Эскиз {entry.Name}";
                break;
            default:
                return;
        }

        AutomationProperties.SetName(image, accessibleName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        _ = LoadThumbnailImageAsync(
            image,
            state,
            state.ExpectedItem,
            path,
            size,
            version,
            cancellation.Token);
    }

    private async Task LoadThumbnailImageAsync(
        Image image,
        ThumbnailImageState state,
        object? expectedItem,
        string path,
        uint size,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await _thumbnailService.GetAsync(
                path,
                size,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || source is null
                || state.Version != version
                || !ReferenceEquals(state.ExpectedItem, expectedItem)
                || !ReferenceEquals(image.Tag, expectedItem)
                || image.XamlRoot is null)
            {
                return;
            }

            SetThumbnailVisualState(image, source);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or UnauthorizedAccessException
            or IOException
            or System.Runtime.InteropServices.COMException
            or ArgumentException
            or ObjectDisposedException)
        {
            if (state.Version == version
                && ReferenceEquals(image.Tag, expectedItem))
            {
                SetThumbnailVisualState(image, null);
            }
        }
    }

    private static void CancelThumbnailLoad(ThumbnailImageState state)
    {
        var cancellation = state.Cancellation;
        state.Cancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void SetThumbnailVisualState(
        Image image,
        ImageSource? source)
    {
        image.Source = source;
        image.Opacity = source is null ? 0 : 1;
        if (image.Parent is Panel panel)
        {
            var fallback = panel.Children
                .OfType<FontIcon>()
                .FirstOrDefault();
            if (fallback is not null)
            {
                fallback.Opacity = source is null ? 1 : 0;
            }
        }
    }

    private static bool IsCachedShellIcon(string? path)
    {
        return path?.Contains(
            $"{Path.DirectorySeparatorChar}IconCache{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? GetGameArtworkSourcePath(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.ArtworkPath)
            && File.Exists(game.ArtworkPath))
        {
            return game.ArtworkPath;
        }

        if (!string.IsNullOrWhiteSpace(game.LaunchTarget)
            && File.Exists(game.LaunchTarget))
        {
            return game.LaunchTarget;
        }

        return null;
    }

    private void ShowGamePreview(GameEntry game)
    {
        _selectedEntry = null;
        _selectedGame = game;
        _currentSuggestion = null;

        PreviewName.Text = game.Name;
        PreviewType.Text = game.SourceLabel;
        PreviewPath.Text = game.InstallPath;
        PreviewModifiedLabel.Text = "Источник";
        PreviewModified.Text = game.SourceLabel;
        PreviewSizeLabel.Text = "Связанный торрент";
        PreviewSize.Text = string.IsNullOrWhiteSpace(game.RelatedTorrentPath)
            ? "Нет"
            : Path.GetFileName(game.RelatedTorrentPath);
        PreviewGlyph.Glyph = game.Glyph;
        PreviewImage.Source = null;
        AutomationProperties.SetName(
            PreviewImage,
            $"Обложка игры {game.Name}");
        PreviewImage.MaxHeight = 230;
        PreviewImage.MaxWidth = double.PositiveInfinity;
        PreviewImage.Stretch = Stretch.Uniform;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;
        _ = LoadGamePreviewArtworkAsync(game);
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        ShowPreviewPanelIfSpaceAvailable();
        OpenPreviewButton.IsEnabled = game.CanLaunch;
        OpenPreviewButton.Content = game.ActionLabel;
        PreviewOpenInExplorerButton.IsEnabled = true;
        GameManagementPanel.Visibility = Visibility.Visible;
        DeleteGameFilesButton.Content = game.IsLocalInstall
            ? game.CanDeleteFiles
                ? "Удалить файлы…"
                : "Проверить файлы"
            : "Управлять установкой";
    }

    private void ClosePreview()
    {
        _selectedEntry = null;
        _selectedGame = null;
        _selectedRecycleBinItem = null;
        _currentSuggestion = null;
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        GameManagementPanel.Visibility = Visibility.Collapsed;
        RecycleBinManagementPanel.Visibility = Visibility.Collapsed;
        PreviewFileActions.Visibility = Visibility.Visible;
        OpenPreviewButton.Visibility = Visibility.Visible;
        PreviewPathLabel.Text = "Расположение";
        PreviewModifiedLabel.Text = "Изменён";
        PreviewSizeLabel.Text = "Размер";
        PreviewImage.Source = null;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewOpenInExplorerButton.IsEnabled = true;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
    }

    private bool ShowPreviewPanelIfSpaceAvailable()
    {
        if (RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < 1320)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewColumn.Width = new GridLength(0);
            return false;
        }

        PreviewColumn.Width = new GridLength(GetPreviewWidth());
        PreviewPanel.Visibility = Visibility.Visible;
        return true;
    }

    private void ShowShellPreview()
    {
        var documentsPath =
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var projectsPath = Path.Combine(documentsPath, "Projects");
        var displayName = string.IsNullOrWhiteSpace(ViewModel.CurrentTitle)
            ? "Проекты"
            : ViewModel.CurrentTitle;
        var isAiCenter = string.Equals(
            displayName,
            "AI-центр",
            StringComparison.CurrentCultureIgnoreCase);
        string targetPath;
        if (isAiCenter)
        {
            try
            {
                targetPath = _aiWorkspaceService
                    .GetSearchRoots()
                    .FirstOrDefault()
                    ?? documentsPath;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                targetPath = documentsPath;
            }
        }
        else
        {
            targetPath = Directory.Exists(projectsPath)
                ? projectsPath
                : documentsPath;
        }

        var displayType = isAiCenter
            ? "Локальный AI-обзор"
            : "Пространство Nexus";
        var glyph = isAiCenter
            ? "\uE945"
            : "\uE8B7";
        _selectedGame = null;
        _selectedEntry = new FileSystemEntry(
            displayName,
            targetPath,
            true,
            DateTimeOffset.Now,
            null,
            displayType,
            glyph);

        PreviewName.Text = displayName;
        PreviewType.Text = displayType;
        PreviewPath.Text = targetPath;
        PreviewModified.Text = isAiCenter
            ? _aiCenterMetadataLoaded
                ? $"Проверено {_aiCenterMetadataLoadedAt.LocalDateTime:t}"
                : "Источники проверяются"
            : "Готово к работе";
        PreviewSize.Text = "—";
        PreviewModifiedLabel.Text = "Изменён";
        PreviewSizeLabel.Text = "Размер";
        PreviewGlyph.Glyph = glyph;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewDocumentCover.Visibility = Visibility.Collapsed;
        PreviewGlyphShell.Visibility = Visibility.Visible;
        ShowPreviewPanelIfSpaceAvailable();
        OpenPreviewButton.IsEnabled = Directory.Exists(targetPath);
        OpenPreviewButton.Content = isAiCenter
            ? "Открыть источник"
            : "Открыть";
        OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
        GameManagementPanel.Visibility = Visibility.Collapsed;
    }

    private async Task LoadDashboardMetadataAsync(
        bool forceAiRefresh = false)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(profile, "Downloads");
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var projects = Path.Combine(documents, "Projects");

        var countsTask = Task.WhenAll(
            CountEntriesAsync(documents),
            CountEntriesAsync(projects),
            CountEntriesAsync(pictures),
            CountEntriesAsync(downloads));
        var aiMetadataTask = LoadAiCenterMetadataAsync(forceAiRefresh);
        var counts = await countsTask;

        DocumentsCount.Text = FormatItemCount(counts[0]);
        WebAppsCount.Text = FormatItemCount(counts[1]);
        ResourcesCount.Text = FormatItemCount(counts[2]);
        DownloadsCount.Text = FormatItemCount(counts[3]);
        WorkspaceDocumentsCount.Text = DocumentsCount.Text;
        WorkspaceWebAppsCount.Text = WebAppsCount.Text;
        WorkspaceResourcesCount.Text = ResourcesCount.Text;
        WorkspaceDownloadsCount.Text = DownloadsCount.Text;

        await aiMetadataTask;
    }

    private static Task<int> CountEntriesAsync(string path)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            try
            {
                return Directory.EnumerateFileSystemEntries(path).Take(1000).Count();
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                return 0;
            }
        });
    }

    private static string FormatItemCount(int count)
    {
        return count switch
        {
            1 => "1 элемент",
            >= 2 and <= 4 => $"{count} элемента",
            _ => $"{count} элементов"
        };
    }

    private async Task LoadAiCenterMetadataAsync(bool force)
    {
        if (_rootUnloaded
            || !force
            && _aiCenterMetadataLoaded
            && DateTimeOffset.Now - _aiCenterMetadataLoadedAt
            <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        var previousCancellation = _aiCenterLoadCancellation;
        var cancellation = new CancellationTokenSource();
        _aiCenterLoadCancellation = cancellation;
        if (previousCancellation is not null)
        {
            previousCancellation.Cancel();
            previousCancellation.Dispose();
        }

        var token = cancellation.Token;
        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var modelRoot = Path.Combine(
            profile,
            ".ollama",
            "models",
            "manifests");
        var skillsRoot = Path.Combine(
            profile,
            ".codex",
            "skills");
        var mcpConfigPath = Path.Combine(
            profile,
            ".codex",
            "config.toml");
        IReadOnlyList<string> projectRoots;
        try
        {
            projectRoots = _aiWorkspaceService.GetSearchRoots();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            projectRoots = [];
        }

        SetAiCenterLoadingState(
            modelRoot,
            skillsRoot,
            mcpConfigPath,
            projectRoots);

        var localSourcesTask = Task.Run(
            () => DiscoverLocalAiSources(
                modelRoot,
                skillsRoot,
                mcpConfigPath,
                token),
            token);
        var projectsTask = _aiWorkspaceService.DiscoverProjectsAsync(token);

        try
        {
            var localSources = await localSourcesTask;
            IReadOnlyList<AiProjectEntry> projects;
            string? projectsError = null;
            try
            {
                projects = await projectsTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                projects = [];
                projectsError = FormatAiSourceError(
                    "AI-проекты",
                    exception);
            }

            token.ThrowIfCancellationRequested();
            if (_rootUnloaded
                || !ReferenceEquals(
                    _aiCenterLoadCancellation,
                    cancellation))
            {
                return;
            }

            ApplyAiResourceCard(
                LocalModelsCount,
                LocalModelsStatus,
                LocalModelsPath,
                LocalModelsProgressRing,
                LocalModelsActionButton,
                localSources.Models,
                count => count switch
                {
                    1 => "локальная модель",
                    >= 2 and <= 4 => "локальные модели",
                    _ => "локальных моделей"
                });
            ApplyAiProjectsCard(
                projects,
                projectRoots,
                projectsError);
            ApplyAiResourceCard(
                SkillsCount,
                SkillsStatus,
                SkillsPath,
                SkillsProgressRing,
                SkillsActionButton,
                localSources.Skills,
                count => count switch
                {
                    1 => "установленный Skill",
                    >= 2 and <= 4 => "установленных Skills",
                    _ => "установленных Skills"
                });
            ApplyAiResourceCard(
                McpCount,
                McpStatus,
                McpPath,
                McpProgressRing,
                McpActionButton,
                localSources.Mcp,
                count => count switch
                {
                    1 => "сервер MCP",
                    >= 2 and <= 4 => "сервера MCP",
                    _ => "серверов MCP"
                });

            var errors = new[]
                {
                    localSources.Models.Error,
                    projectsError,
                    localSources.Skills.Error,
                    localSources.Mcp.Error
                }
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Cast<string>()
                .ToArray();
            AiCenterErrorBar.Message = string.Join(
                Environment.NewLine,
                errors);
            AiCenterErrorBar.IsOpen = errors.Length > 0;
            _aiCenterMetadataLoaded = true;
            _aiCenterMetadataLoadedAt = DateTimeOffset.Now;
            AiCenterLastCheckedText.Text =
                $"Обновлено {_aiCenterMetadataLoadedAt.LocalDateTime:t}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_rootUnloaded
                && ReferenceEquals(
                    _aiCenterLoadCancellation,
                    cancellation))
            {
                SetAiCenterFailureState(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(
                    _aiCenterLoadCancellation,
                    cancellation))
            {
                AiCenterRefreshButton.IsEnabled = true;
                _aiCenterLoadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void SetAiCenterLoadingState(
        string modelRoot,
        string skillsRoot,
        string mcpConfigPath,
        IReadOnlyList<string> projectRoots)
    {
        AiCenterRefreshButton.IsEnabled = false;
        AiCenterErrorBar.IsOpen = false;
        AiCenterLastCheckedText.Text = "Проверка источников…";

        SetAiCardLoading(
            LocalModelsCount,
            LocalModelsStatus,
            LocalModelsPath,
            LocalModelsProgressRing,
            LocalModelsActionButton,
            "Проверяем Ollama",
            modelRoot);
        SetAiCardLoading(
            AiProjectsCount,
            AiProjectsStatus,
            AiProjectsPath,
            AiProjectsProgressRing,
            AiProjectsActionButton,
            "Ищем рабочие пространства",
            projectRoots.FirstOrDefault()
            ?? "Доступные папки не найдены");
        AiProjectsPathLabel.Text = "ИСТОЧНИКИ ПОИСКА";
        ToolTipService.SetToolTip(
            AiProjectsPath,
            projectRoots.Count == 0
                ? null
                : string.Join(Environment.NewLine, projectRoots));
        SetAiCardLoading(
            SkillsCount,
            SkillsStatus,
            SkillsPath,
            SkillsProgressRing,
            SkillsActionButton,
            "Проверяем Codex",
            skillsRoot);
        SetAiCardLoading(
            McpCount,
            McpStatus,
            McpPath,
            McpProgressRing,
            McpActionButton,
            "Читаем config.toml",
            mcpConfigPath);
    }

    private static void SetAiCardLoading(
        TextBlock countText,
        TextBlock statusText,
        TextBlock pathText,
        ProgressRing progressRing,
        Button actionButton,
        string status,
        string path)
    {
        countText.Text = "—";
        statusText.Text = status;
        SetAiCardPath(pathText, path, path);
        progressRing.Visibility = Visibility.Visible;
        progressRing.IsActive = true;
        actionButton.IsEnabled = false;
    }

    private void ApplyAiProjectsCard(
        IReadOnlyList<AiProjectEntry> projects,
        IReadOnlyList<string> roots,
        string? error)
    {
        AiProjectsCount.Text = projects.Count.ToString();
        AiProjectsProgressRing.IsActive = false;
        AiProjectsProgressRing.Visibility = Visibility.Collapsed;
        AiProjectsActionButton.IsEnabled = error is null;

        if (error is not null)
        {
            AiProjectsStatus.Text = "Ошибка проверки";
            AiProjectsPathLabel.Text = "ИСТОЧНИКИ ПОИСКА";
            var rootPath = roots.FirstOrDefault()
                ?? "Доступные папки не найдены";
            SetAiCardPath(
                AiProjectsPath,
                rootPath,
                roots.Count == 0
                    ? rootPath
                    : string.Join(Environment.NewLine, roots));
            return;
        }

        AiProjectsStatus.Text = projects.Count switch
        {
            0 => roots.Count == 0
                ? "источники не найдены"
                : $"проверено {FormatPathCount(roots.Count)}",
            1 => "AI-проект",
            >= 2 and <= 4 => "AI-проекта",
            _ => "AI-проектов"
        };
        var latestProject = projects.FirstOrDefault();
        AiProjectsPathLabel.Text = latestProject is null
            ? "ИСТОЧНИКИ ПОИСКА"
            : "ПОСЛЕДНИЙ ПРОЕКТ";
        var visiblePath = latestProject?.FullPath
            ?? roots.FirstOrDefault()
            ?? "Доступные папки не найдены";
        var fullPathList = latestProject is not null
            ? latestProject.FullPath
            : roots.Count == 0
                ? visiblePath
                : string.Join(Environment.NewLine, roots);
        SetAiCardPath(
            AiProjectsPath,
            visiblePath,
            fullPathList);
    }

    private static void ApplyAiResourceCard(
        TextBlock countText,
        TextBlock statusText,
        TextBlock pathText,
        ProgressRing progressRing,
        Button actionButton,
        AiResourceSnapshot snapshot,
        Func<int, string> countLabel)
    {
        countText.Text = snapshot.Count.ToString();
        progressRing.IsActive = false;
        progressRing.Visibility = Visibility.Collapsed;
        statusText.Text = snapshot.Error is not null
            ? "Ошибка чтения"
            : snapshot.Count == 0
                ? "ничего не найдено"
                : countLabel(snapshot.Count);
        SetAiCardPath(
            pathText,
            snapshot.Path,
            snapshot.Path);
        actionButton.IsEnabled = snapshot.SourceExists
            && snapshot.Error is null;
    }

    private void SetAiCenterFailureState(Exception exception)
    {
        var message = FormatAiSourceError(
            "AI-центр",
            exception);
        foreach (var (status, progress, action) in new[]
                 {
                     (LocalModelsStatus, LocalModelsProgressRing, LocalModelsActionButton),
                     (AiProjectsStatus, AiProjectsProgressRing, AiProjectsActionButton),
                     (SkillsStatus, SkillsProgressRing, SkillsActionButton),
                     (McpStatus, McpProgressRing, McpActionButton)
                 })
        {
            status.Text = "Ошибка проверки";
            progress.IsActive = false;
            progress.Visibility = Visibility.Collapsed;
            action.IsEnabled = false;
        }

        AiCenterErrorBar.Message = message;
        AiCenterErrorBar.IsOpen = true;
        AiCenterLastCheckedText.Text = "Проверка не завершена";
    }

    private static LocalAiSourcesSnapshot DiscoverLocalAiSources(
        string modelRoot,
        string skillsRoot,
        string mcpConfigPath,
        CancellationToken cancellationToken)
    {
        return new LocalAiSourcesSnapshot(
            CountAiFiles(
                "Локальные модели",
                modelRoot,
                "*",
                SearchOption.AllDirectories,
                cancellationToken),
            CountAiFiles(
                "Skills",
                skillsRoot,
                "SKILL.md",
                SearchOption.AllDirectories,
                cancellationToken),
            CountMcpServers(
                mcpConfigPath,
                cancellationToken));
    }

    private static AiResourceSnapshot CountAiFiles(
        string sourceName,
        string path,
        string searchPattern,
        SearchOption searchOption,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return new AiResourceSnapshot(0, path, false, null);
        }

        try
        {
            var count = 0;
            foreach (var _ in Directory.EnumerateFiles(
                         path,
                         searchPattern,
                         searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
            }

            return new AiResourceSnapshot(count, path, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return new AiResourceSnapshot(
                0,
                path,
                true,
                FormatAiSourceError(sourceName, exception));
        }
    }

    private static AiResourceSnapshot CountMcpServers(
        string configPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return new AiResourceSnapshot(
                0,
                configPath,
                false,
                null);
        }

        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(configPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.TrimStart().StartsWith(
                        "[mcp_servers.",
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return new AiResourceSnapshot(
                count,
                configPath,
                true,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return new AiResourceSnapshot(
                0,
                configPath,
                true,
                FormatAiSourceError("MCP", exception));
        }
    }

    private static void SetAiCardPath(
        TextBlock pathText,
        string displayPath,
        string toolTip)
    {
        pathText.Text = displayPath;
        ToolTipService.SetToolTip(pathText, toolTip);
        AutomationProperties.SetName(
            pathText,
            $"Источник: {displayPath}");
    }

    private static string FormatPathCount(int count)
    {
        return count switch
        {
            1 => "1 путь",
            >= 2 and <= 4 => $"{count} пути",
            _ => $"{count} путей"
        };
    }

    private static string FormatAiSourceError(
        string sourceName,
        Exception exception)
    {
        var reason = exception is UnauthorizedAccessException
            ? "нет доступа"
            : "ошибка чтения";
        return $"{sourceName}: {reason}.";
    }

    private sealed record AiResourceSnapshot(
        int Count,
        string Path,
        bool SourceExists,
        string? Error);

    private sealed record LocalAiSourcesSnapshot(
        AiResourceSnapshot Models,
        AiResourceSnapshot Skills,
        AiResourceSnapshot Mcp);

    private void UpdateOrganizationSuggestion(FileSystemEntry entry)
    {
        _currentSuggestion = entry.IsDirectory
            ? null
            : _organizationService.Suggest(entry.FullPath);

        if (_currentSuggestion is null)
        {
            OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SuggestedDestination.Text = _currentSuggestion.DestinationLabel;
        SuggestedReason.Text = _currentSuggestion.Reason;
        SuggestedConfidence.Text = $"Уверенность: {_currentSuggestion.ConfidencePercent}%";
        OrganizationSuggestionPanel.Visibility = Visibility.Visible;
    }

    private async void ApplySuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSuggestion is null)
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Переместить файл?",
            Content = new TextBlock
            {
                Text = $"{Path.GetFileName(_currentSuggestion.SourcePath)}\n→ {_currentSuggestion.DestinationLabel}\n\n{_currentSuggestion.Reason}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            PrimaryButtonText = "Переместить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ApplySuggestionButton.IsEnabled = false;
        try
        {
            var result = await _organizationService.ApplyAsync(_currentSuggestion);
            if (!result.Success)
            {
                ShowOperationError(result.Message);
                return;
            }

            _lastOrganizationOperation = result.Operation;
            UndoSuggestionButton.Visibility = result.Operation is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            OrganizationSuggestionPanel.Visibility = Visibility.Collapsed;
            ClosePreview();
            await ViewModel.RefreshAsync();
            CaptureActiveSession();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException)
        {
            ShowOperationError(
                "Не удалось переместить файл. Исходный файл оставлен без изменений.");
        }
        finally
        {
            ApplySuggestionButton.IsEnabled = true;
        }
    }

    private async void UndoSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOrganizationOperation is null)
        {
            return;
        }

        UndoSuggestionButton.IsEnabled = false;
        try
        {
            var result = await _organizationService.UndoAsync(_lastOrganizationOperation);
            if (!result.Success)
            {
                ShowOperationError(result.Message);
                return;
            }

            _lastOrganizationOperation = null;
            UndoSuggestionButton.Visibility = Visibility.Collapsed;
            await ViewModel.RefreshAsync();
            CaptureActiveSession();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException)
        {
            ShowOperationError(
                "Не удалось отменить перемещение. Проверьте исходную и конечную папки.");
        }
        finally
        {
            UndoSuggestionButton.IsEnabled = true;
        }
    }

    private void SetNavigationSelection(Button selectedButton)
    {
        if (!ReferenceEquals(selectedButton, RecycleNavigationButton))
        {
            LeaveRecycleBinMode();
        }

        if (!ReferenceEquals(selectedButton, SettingsNavigationButton))
        {
            LeaveSettingsMode();
        }

        var selectedBrush = (Brush)Application.Current.Resources["NexusSelectedBrush"];
        var accentBrush = (Brush)Application.Current.Resources["NexusAccentBrush"];
        var secondaryBrush = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"];

        foreach (var button in _navigationButtons)
        {
            var isSelected = ReferenceEquals(button, selectedButton);
            button.Background = isSelected
                ? selectedBrush
                : new SolidColorBrush(Colors.Transparent);
            button.Foreground = isSelected ? accentBrush : secondaryBrush;
        }
    }

    private void SetNavigationSelectionById(string id)
    {
        var button = _navigationButtons.FirstOrDefault(item =>
            string.Equals(item.Tag as string, id, StringComparison.Ordinal));

        if (button is not null)
        {
            SetNavigationSelection(button);
        }
    }

    private void ClearNavigationSelection()
    {
        LeaveRecycleBinMode();
        LeaveSettingsMode();
        var secondaryBrush = (Brush)Application.Current.Resources["NexusTextSecondaryBrush"];

        foreach (var button in _navigationButtons)
        {
            button.Background = new SolidColorBrush(Colors.Transparent);
            button.Foreground = secondaryBrush;
        }
    }

    private void LeaveRecycleBinMode()
    {
        if (!_isRecycleBinActive)
        {
            return;
        }

        _isRecycleBinActive = false;
        _searchDebounceTimer.Stop();
        _recycleBinLoadCancellation?.Cancel();
        RecycleBinListView.SelectedItems.Clear();
        RecycleBinGridView.SelectedItems.Clear();
        ClosePreview();
    }

    private void LeaveSettingsMode()
    {
        if (!_isSettingsActive)
        {
            return;
        }

        _isSettingsActive = false;
        SettingsInfoBar.IsOpen = false;
    }

    private void ShowOperationError(string message)
    {
        _operationErrorMessage = message;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private void ErrorInfoBar_CloseButtonClick(
        InfoBar sender,
        object args)
    {
        _operationErrorMessage = null;
    }

    private void ClearOperationError()
    {
        _operationErrorMessage = null;
        ErrorInfoBar.IsOpen = false;
    }

    private static string GetPreviewBadge(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" => "W",
            ".xls" or ".xlsx" => "X",
            ".ppt" or ".pptx" => "P",
            ".pdf" => "PDF",
            ".zip" or ".7z" or ".rar" => "ZIP",
            ".fig" => "F",
            _ => "N"
        };
    }

    private double GetPreviewWidth()
    {
        if (RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < 1320)
        {
            return _isWorkspaceTabActive ? 300 : 288;
        }

        if (_isRecycleBinActive)
        {
            return RootGrid.ActualWidth >= 1900 ? 360 : 320;
        }

        return _isWorkspaceTabActive ? 340 : 320;
    }

    private static bool IsImage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }

    private sealed class ThumbnailImageState
    {
        public bool IsTrackingTag { get; set; }

        public long TagCallbackToken { get; set; }

        public long Version { get; set; }

        public object? ExpectedItem { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed class WorkspaceTabState
    {
        public WorkspaceTabState(BrowserSessionState session)
        {
            Session = session;
            Label = GetTabLabel(session.Current.Title);
            Glyph = session.Current.Kind switch
            {
                NavigationKind.Games => "\uE7FC",
                NavigationKind.Applications => "\uECAA",
                NavigationKind.Computer => "\uE7F8",
                NavigationKind.Network => "\uE968",
                _ => "\uE8A5"
            };
        }

        public Guid Id { get; } = Guid.NewGuid();

        public BrowserSessionState Session { get; set; }

        public string Label { get; set; }

        public string Glyph { get; set; }

        public bool GridMode { get; set; }

        public IReadOnlyList<string> SelectedPaths { get; set; } = [];
    }

    private readonly record struct EntryCapabilities(
        bool CanOpen,
        bool CanCopy,
        bool CanCut,
        bool CanRename,
        bool CanDelete,
        bool CanFavorite,
        bool CanShowProperties);

    private enum ClipboardOperation
    {
        None,
        Copy,
        Move
    }
}

public sealed class RecycleBinListItem
{
    public RecycleBinListItem(RecycleBinEntry entry)
    {
        Entry = entry;
    }

    public RecycleBinEntry Entry { get; }

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string OriginalPathDisplay =>
        string.IsNullOrWhiteSpace(Entry.OriginalPath)
            ? "Исходное расположение неизвестно"
            : Entry.OriginalPath;

    public string OriginalFolderDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.OriginalPath))
            {
                return "Расположение неизвестно";
            }

            try
            {
                return Path.GetDirectoryName(Entry.OriginalPath)
                    ?? Entry.OriginalPath;
            }
            catch (ArgumentException)
            {
                return Entry.OriginalPath;
            }
        }
    }

    public string DeletedDisplay => Entry.DeletedAt is null
        ? "Дата неизвестна"
        : Entry.DeletedAt.Value.LocalDateTime.ToString("dd.MM.yyyy  HH:mm");

    public string DisplayType => string.IsNullOrWhiteSpace(Entry.DisplayType)
        ? Entry.IsDirectory ? "Папка" : "Файл"
        : Entry.DisplayType;

    public long? SizeBytes => Entry.SizeBytes;

    public string SizeDisplay => Entry.SizeBytes is null
        ? "—"
        : FormatSize(Entry.SizeBytes.Value);

    public bool IsDirectory => Entry.IsDirectory;

    public string Glyph => Entry.IsDirectory ? "\uE8B7" : "\uE8A5";

    public string AutomationName =>
        $"{Name}, {DisplayType}, удалён {DeletedDisplay}, " +
        $"исходное расположение {OriginalPathDisplay}, размер {SizeDisplay}";

    private static string FormatSize(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }
}

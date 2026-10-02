using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nexus.App.Services;
using Nexus.App.Views;
using Nexus.Core.Integration;
using Nexus.Core.IO;
using Nexus.Core.Settings;
using Nexus.Core.Shell;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Nexus.App.Shell;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MinimumWidth = 960;
    private const int MinimumHeight = 600;
    private readonly WindowContext _context;
    private readonly SettingsStore _settings;
    private readonly ElementTheme _themeOverride;
    private readonly DispatcherQueueTimerHolder _searchDebounce;
    private bool _suppressSearchEvents;
    private RectInt32? _normalBounds;
    private WindowMaterial? _appliedMaterial;

    public MainWindow(ShellViewModel viewModel, WindowContext context, SettingsStore settings, StartupOptions options)
    {
        ViewModel = viewModel;
        _context = context;
        _settings = settings;
        InitializeComponent();

        Title = "Nexus";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        _themeOverride = options.Theme;
        ApplyTheme();
        ApplyMaterial();
        _settings.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTheme();
            ApplyMaterial();
            ApplySidebarSections();
        });
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += (_, _) => SaveSession();

        _searchDebounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(220), () =>
            CurrentPage?.OnSearchTextChanged(SearchBox.Text));

        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        RootGrid.Loaded += (_, _) =>
        {
            _context.Attach(this, RootGrid.XamlRoot);
            UpdateCaptionButtonColors();
            ApplyWindowBounds();
        };

        BuildSidebarLocations();
        ApplySidebarSections();
        ViewModel.LocationChanged += (_, location) => ShowLocation(location);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        var start = options.Page switch
        {
            null or "" => NavLocation.Home,
            "documents" => NavLocation.ForFolder(KnownFolders.GetPath(KnownFolder.Documents)),
            var page when Directory.Exists(page) => NavLocation.ForFolder(page),
            var page => NavLocation.FromTag(page)
        };
        if (start != NavLocation.Home)
        {
            ViewModel.Navigate(start);
        }

        ShowLocation(ViewModel.SelectedTab!.Location);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellViewModel ViewModel { get; }

    public bool IsSearchAvailable => CurrentPage?.SupportsSearch ?? false;

    private IShellPage? CurrentPage => ContentFrame.Content as IShellPage;

    private void BuildSidebarLocations()
    {
        var insertAt = Sidebar.MenuItems.IndexOf(FoldersHeader) + 1;
        foreach (var folder in KnownFolders.UserFolders.Where(folder => folder.Folder != KnownFolder.Profile))
        {
            Sidebar.MenuItems.Insert(insertAt++, new NavigationViewItem
            {
                Content = folder.Title,
                Tag = $"folder:{folder.Path}",
                Icon = new FontIcon { Glyph = folder.Glyph }
            });
        }

        // Drives appear right under "Этот компьютер", like the Explorer navigation pane.
        insertAt = Sidebar.MenuItems.IndexOf(ThisPcItem) + 1;
        foreach (var drive in Drives.GetReady(includeNetwork: false))
        {
            Sidebar.MenuItems.Insert(insertAt++, new NavigationViewItem
            {
                Content = drive.DisplayName,
                Tag = $"folder:{drive.RootPath}",
                Icon = new FontIcon { Glyph = drive.Glyph }
            });
        }
    }

    /// <summary>Sections turned off in Настройки disappear from the sidebar; the header goes when all of them do.</summary>
    private void ApplySidebarSections()
    {
        var hidden = _settings.Current.HiddenSidebarSections;
        foreach (var item in Sidebar.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && AppSettings.OptionalSidebarSections.Contains(tag))
            {
                item.Visibility = hidden.Contains(tag) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        LibrariesHeader.Visibility = AppSettings.OptionalSidebarSections.All(hidden.Contains) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A plain launch (no folder or section asked for) opens what Настройки › Запуск says.</summary>
    public void ApplyStartupPage()
    {
        switch (_settings.Current.StartupPage)
        {
            case StartupPage.ThisPc:
                ViewModel.Navigate(NavLocation.FromTag("thispc"));
                break;
            case StartupPage.Downloads:
                ViewModel.Navigate(NavLocation.ForFolder(KnownFolders.GetPath(KnownFolder.Downloads)));
                break;
            case StartupPage.LastSession:
                RestoreSession();
                break;
        }
    }

    private void RestoreSession()
    {
        var locations = _settings.Current.LastSessionTabs.Select(SessionLocation).OfType<NavLocation>().ToArray();
        if (locations.Length == 0)
        {
            return;
        }

        ViewModel.Navigate(locations[0]);
        foreach (var location in locations.Skip(1))
        {
            ViewModel.OpenInNewTab(location);
        }

        ViewModel.SelectedTab = ViewModel.Tabs[Math.Clamp(_settings.Current.LastSessionSelectedTab, 0, ViewModel.Tabs.Count - 1)];
    }

    /// <summary>A saved tab; folders that are gone are skipped (network paths are kept: checking them can hang).</summary>
    private static NavLocation? SessionLocation(string tag)
    {
        if (!tag.StartsWith("folder:", StringComparison.Ordinal))
        {
            return NavLocation.FromTag(tag);
        }

        var path = tag["folder:".Length..];
        return path.StartsWith(@"\\", StringComparison.Ordinal) || Directory.Exists(path) ? NavLocation.ForFolder(path) : null;
    }

    private void SaveSession()
    {
        var tabs = ViewModel.Tabs.Select(tab => tab.Location.SessionTag).ToList();
        var selected = ViewModel.SelectedTab is { } current ? Math.Max(0, ViewModel.Tabs.IndexOf(current)) : 0;
        var bounds = _normalBounds ?? new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _settings.Update(settings =>
        {
            settings.LastSessionTabs = tabs;
            settings.LastSessionSelectedTab = selected;
            settings.Window = new WindowPlacement
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = maximized
            };
        });
    }

    /// <summary>Remembers the last normal bounds, so a maximized window comes back to them when restored next time.</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void ShowLocation(NavLocation location)
    {
        var pageType = location.Kind switch
        {
            PageKind.Home => typeof(HomePage),
            PageKind.ThisPc => typeof(ThisPcPage),
            PageKind.RecycleBin => typeof(RecycleBinPage),
            PageKind.Network => typeof(NetworkPage),
            PageKind.Apps => typeof(AppsPage),
            PageKind.Games => typeof(GamesPage),
            PageKind.AiCenter => typeof(AiCenterPage),
            PageKind.Torrents => typeof(TorrentsPage),
            PageKind.Organizer => typeof(OrganizerPage),
            PageKind.Settings => typeof(SettingsPage),
            _ => typeof(FolderPage)
        };

        EndAddressEdit();
        _suppressSearchEvents = true;
        SearchBox.Text = string.Empty;
        _suppressSearchEvents = false;
        ContentFrame.Navigate(pageType, location, new SuppressNavigationTransitionInfo());
        SelectSidebarItem(location);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchAvailable)));
    }

    private void SelectSidebarItem(NavLocation location)
    {
        var tag = location.SidebarTag;
        var item = Sidebar.MenuItems
            .Concat(Sidebar.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => candidate.Tag is string candidateTag
                && (string.Equals(candidateTag, tag, StringComparison.OrdinalIgnoreCase)
                    || (candidateTag.StartsWith("folder:", StringComparison.Ordinal) && location.Path is { } path
                        && PathHelper.AreEqual(candidateTag["folder:".Length..], path))));
        Sidebar.SelectedItem = item ?? NoSelectionItem;
    }

    private void Sidebar_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag)
        {
            ViewModel.Navigate(NavLocation.FromTag(tag));
        }
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbSegment segment)
        {
            ViewModel.Navigate(segment.Location);
        }
    }

    private void TabStrip_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewModel tab)
        {
            ViewModel.CloseTab(tab);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => CurrentPage?.Refresh();

    // ---------- Notifications ----------

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsNotificationOpen) or nameof(ShellViewModel.NotificationMessage))
        {
            UpdateNotification();
        }
    }

    private void UpdateNotification()
    {
        Notification.Title = ViewModel.NotificationTitle;
        Notification.Message = ViewModel.NotificationMessage;
        Notification.Severity = ViewModel.NotificationSeverity;
        NotificationAction.Content = ViewModel.NotificationActionText;
        NotificationAction.Visibility = ViewModel.HasNotificationAction ? Visibility.Visible : Visibility.Collapsed;
        Notification.IsOpen = ViewModel.IsNotificationOpen;
        NotificationHost.Visibility = ViewModel.IsNotificationOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Notification_CloseButtonClick(InfoBar sender, object args) => ViewModel.IsNotificationOpen = false;

    private void NotificationAction_Click(object sender, RoutedEventArgs e) => ViewModel.RunNotificationActionCommand.Execute(null);

    // ---------- Address bar ----------

    private void AddressBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicks on a breadcrumb segment navigate; clicks on empty space edit the path.
        if (IsInside<BreadcrumbBarItem>(e.OriginalSource as DependencyObject) || AddressBox.Visibility == Visibility.Visible)
        {
            return;
        }

        BeginAddressEdit();
    }

    private void BeginAddressEdit()
    {
        var location = ViewModel.SelectedTab?.Location;
        AddressBox.Text = location?.Path ?? location?.Title ?? string.Empty;
        AddressBox.Visibility = Visibility.Visible;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            NavigateToTypedAddress(AddressBox.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndAddressEdit();
            CurrentPage?.FocusContent();
        }
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e) => EndAddressEdit();

    private void NavigateToTypedAddress(string text)
    {
        var input = text.Trim().Trim('"');
        if (input.Length == 0)
        {
            EndAddressEdit();
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(input);
        try
        {
            if (PathHelper.IsNetworkComputer(expanded))
            {
                // \\server is not a folder Windows can enumerate: Nexus lists its shared folders.
                EndAddressEdit();
                ViewModel.Navigate(NavLocation.ForFolder(expanded));
            }
            else if (Directory.Exists(expanded))
            {
                EndAddressEdit();
                ViewModel.Navigate(NavLocation.ForFolder(PathHelper.Normalize(expanded)));
            }
            else if (File.Exists(expanded))
            {
                EndAddressEdit();
                ShellLauncher.Open(expanded);
            }
            else if (input.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(input, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                EndAddressEdit();
                ShellLauncher.OpenUri(input);
            }
            else
            {
                ViewModel.NotifyError($"Windows не удаётся найти «{input}». Проверьте написание и повторите попытку.", "Путь не найден");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or ArgumentException)
        {
            ViewModel.NotifyError(exception.Message, "Путь не найден");
        }
    }

    private static bool IsInside<T>(DependencyObject? element)
        where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    // ---------- Search ----------

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_suppressSearchEvents && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchDebounce.Restart();
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        CurrentPage?.OnSearchSubmitted(args.QueryText);
    }

    /// <summary>Esc clears the query (and with it the search results); a second Esc returns to the file list.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        _searchDebounce.Stop();
        if (SearchBox.Text.Length > 0)
        {
            _suppressSearchEvents = true;
            SearchBox.Text = string.Empty;
            _suppressSearchEvents = false;
            CurrentPage?.OnSearchTextChanged(string.Empty);
        }
        else
        {
            CurrentPage?.FocusContent();
        }
    }

    // ---------- Keyboard and mouse ----------

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Mouse side buttons: back / forward, like in Explorer.
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (properties.IsXButton1Pressed)
        {
            ViewModel.SelectedTab?.GoBack();
            e.Handled = true;
        }
        else if (properties.IsXButton2Pressed)
        {
            ViewModel.SelectedTab?.GoForward();
            e.Handled = true;
        }
    }

    private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.NewTab();
        args.Handled = true;
    }

    private void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SelectedTab is { } tab)
        {
            ViewModel.CloseTab(tab);
        }

        args.Handled = true;
    }

    private void NextTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CycleTab(1);
        args.Handled = true;
    }

    private void PreviousTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CycleTab(-1);
        args.Handled = true;
    }

    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SelectedTab?.GoBack();
        args.Handled = true;
    }

    private void ForwardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SelectedTab?.GoForward();
        args.Handled = true;
    }

    private void UpAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SelectedTab?.GoUp();
        args.Handled = true;
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsSearchAvailable)
        {
            SearchBox.Focus(FocusState.Keyboard);
        }

        args.Handled = true;
    }

    private void AddressAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        BeginAddressEdit();
        args.Handled = true;
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CurrentPage?.Refresh();
        args.Handled = true;
    }

    // ---------- Launch requests ----------

    /// <summary>Opens what a command line asked for: a folder, an item to select, or a section.</summary>
    public void HandleLaunch(LaunchRequest request, bool newTab)
    {
        NavLocation? location = request.Target switch
        {
            LaunchTarget.Folder when request.Path is not null => NavLocation.ForFolder(request.Path),
            LaunchTarget.SelectItem when request.Path is not null && PathHelper.GetParent(request.Path) is { } parent => NavLocation.ForFolder(parent),
            LaunchTarget.Section when request.Section is not null => NavLocation.FromTag(request.Section),
            _ => newTab ? NavLocation.Home : null
        };

        if (location is null)
        {
            return;
        }

        if (request.Target == LaunchTarget.SelectItem && request.Path is not null)
        {
            ViewModel.RequestSelection(request.Path);
        }

        if (newTab)
        {
            ViewModel.OpenInNewTab(location);
        }
        else
        {
            ViewModel.Navigate(location);
        }
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _themeOverride != ElementTheme.Default
            ? _themeOverride
            : _settings.Current.Theme switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
    }

    /// <summary>Window material and surface opacity from Настройки › Внешний вид; high contrast always stays solid.</summary>
    private void ApplyMaterial()
    {
        var material = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? WindowMaterial.Standard : _settings.Current.Material;
        var transparency = Math.Clamp(_settings.Current.Transparency, 0, 100) / 100.0;
        if (material != _appliedMaterial)
        {
            SystemBackdrop = material switch
            {
                WindowMaterial.Mica => new TransparentBackdrop(glass: false),
                WindowMaterial.Glass => new TransparentBackdrop(glass: true),
                _ => new MicaBackdrop()
            };
            _appliedMaterial = material;
        }

        if (SystemBackdrop is TransparentBackdrop transparent)
        {
            transparent.Transparency = transparency;
        }

        SetSurfaceOpacity(material switch
        {
            WindowMaterial.Mica => 1 - transparency,
            WindowMaterial.Glass => 0.6 * (1 - transparency),
            _ => 1.0
        });
    }

    /// <summary>The toolbar and workspace brushes are shared theme resources: their opacity changes every surface at once.</summary>
    private static void SetSurfaceOpacity(double opacity)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var theme in new[] { "Default", "Light" })
            {
                if (!dictionary.ThemeDictionaries.TryGetValue(theme, out var value) || value is not ResourceDictionary themed)
                {
                    continue;
                }

                foreach (var key in new[] { "NxChromeBackgroundBrush", "NxWorkspaceBackgroundBrush" })
                {
                    if (themed.TryGetValue(key, out var brush) && brush is SolidColorBrush solid)
                    {
                        solid.Opacity = opacity;
                    }
                }
            }
        }
    }

    // ---------- Window ----------

    private void ApplyWindowBounds()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }

        // The last bounds come back when their title bar is still on some screen (a monitor may be gone).
        if (_settings.Current.Window is { Width: > 0, Height: > 0 } saved
            && DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, (int)(48 * scale)), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new RectInt32(
                saved.X,
                saved.Y,
                Math.Max(saved.Width, (int)(MinimumWidth * scale)),
                Math.Max(saved.Height, (int)(MinimumHeight * scale))));
            if (saved.IsMaximized && AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.Maximize();
            }

            return;
        }

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1440 * scale), workArea.Width - (int)(48 * scale));
        var height = Math.Min((int)(900 * scale), workArea.Height - (int)(48 * scale));
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
    }

    private void UpdateCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x87, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x72, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
    }

    /// <summary>A restartable one-shot timer on the UI thread (search debounce).</summary>
    private sealed class DispatcherQueueTimerHolder
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action action)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = interval;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => action();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}

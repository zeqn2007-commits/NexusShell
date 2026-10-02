using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Shell;
using Windows.System;

namespace Nexus.App.Views;

public sealed partial class HomePage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();
    private bool _isActive;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = string.Empty;
        _shell.SelectionText = string.Empty;
        await LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
    }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        await ViewModel.LoadAsync();
        if (!_isActive)
        {
            // The user already went elsewhere: the status bar belongs to that page now.
            return;
        }

        _shell.StatusText = $"{ViewModel.QuickAccess.Count} папок в быстром доступе";
        var size = (int)Math.Ceiling(40 * (XamlRoot?.RasterizationScale ?? 1.0));
        foreach (var item in ViewModel.QuickAccess.Where(item => item.Icon is null).ToArray())
        {
            // ReadOnly marks the folder as customised so it gets its own (known-folder) icon.
            item.Icon = await _icons.GetIconAsync(item.Path, true, FileAttributes.ReadOnly, size);
        }

        foreach (var drive in ViewModel.Drives.Where(drive => drive.Icon is null).ToArray())
        {
            drive.Icon = await _icons.GetIconAsync(drive.RootPath, true, FileAttributes.Directory, size);
        }
    }

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private void QuickAccess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuickAccessItem item })
        {
            _shell.Navigate(NavLocation.ForFolder(item.Path));
        }
    }

    private void QuickAccess_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuickAccessItem item } element)
        {
            return;
        }

        var menu = new MenuFlyout();
        AddMenuItem(menu, "Открыть", "\uE8E5", () => _shell.Navigate(NavLocation.ForFolder(item.Path)));
        AddMenuItem(menu, "Открыть в новой вкладке", "\uE7C4", () => _shell.OpenInNewTab(NavLocation.ForFolder(item.Path)));
        AddMenuItem(menu, "Показать в Проводнике", "\uEC50", () => ShellLauncher.ShowInExplorer(item.Path));
        if (item.IsPinned)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(menu, "Открепить", "\uE77A", () => ViewModel.Unpin(item));
        }

        if (e.TryGetPosition(element, out var point))
        {
            menu.ShowAt(element, point);
        }
        else
        {
            menu.ShowAt(element);
        }

        e.Handled = true;
    }

    private static void AddMenuItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async void FileListSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == RecentSelectorItem)
        {
            await ViewModel.ShowRecentAsync();
        }
        else
        {
            await ViewModel.ShowFavoritesAsync();
        }
    }

    private void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is FileItemViewModel item && item.TryBeginIconRequest())
        {
            _ = LoadIconAsync(item);
        }
    }

    private async Task LoadIconAsync(FileItemViewModel item)
    {
        var size = (int)Math.Ceiling(20 * (XamlRoot?.RasterizationScale ?? 1.0));
        item.Icon = await _icons.GetIconAsync(item.Path, item.IsFolder, item.Entry.Attributes, size);
    }

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel item)
        {
            Open(item);
        }
    }

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FileListView.SelectedItem is FileItemViewModel item)
        {
            Open(item);
            e.Handled = true;
        }
    }

    private void Open(FileItemViewModel item)
    {
        try
        {
            if (item.IsFolder)
            {
                _shell.Navigate(NavLocation.ForFolder(item.Path));
            }
            else
            {
                ShellLauncher.Open(item.Path);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            _shell.NotifyError($"Windows не смогла открыть «{item.Name}»: {exception.Message}");
        }
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e) =>
        _shell.Navigate(NavLocation.FromTag(FileListSelector.SelectedItem == RecentSelectorItem ? "recent" : "favorites"));

    private void ThisPc_Click(object sender, RoutedEventArgs e) => _shell.Navigate(NavLocation.FromTag("thispc"));

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string root })
        {
            _shell.Navigate(NavLocation.ForFolder(root));
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public sealed partial class TorrentsPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();
    private bool _isActive;

    public TorrentsPage()
    {
        ViewModel = App.Services.GetRequiredService<TorrentsViewModel>();
        InitializeComponent();
    }

    public TorrentsViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = "Загрузка…";
        _shell.SelectionText = string.Empty;
        await LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
    }

    public void Refresh() => _ = LoadAsync();

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private async Task LoadAsync()
    {
        await ViewModel.LoadAsync();
        UpdateStatus();
        if (ViewModel.Client is { } client && ViewModel.ClientIcon is null)
        {
            var size = (int)Math.Ceiling(16 * (XamlRoot?.RasterizationScale ?? 1.0));
            ViewModel.ClientIcon = await _icons.GetIconAsync(client.Executable, false, FileAttributes.Normal, size);
        }
    }

    private void UpdateStatus()
    {
        if (_isActive)
        {
            // The user may already be elsewhere: the status bar then belongs to that page.
            _shell.StatusText = ViewModel.StatusText;
        }
    }

    private static T? ItemOf<T>(object sender)
        where T : class => (sender as FrameworkElement)?.Tag as T;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenClient_Click(object sender, RoutedEventArgs e) => ViewModel.OpenClient();

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<TorrentDownloadItem>(sender) is { } item)
        {
            ViewModel.Open(item);
        }
    }

    private void OpenTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<TorrentFileItem>(sender) is { } item)
        {
            ViewModel.OpenFile(item);
        }
    }

    private async void DeleteTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<TorrentFileItem>(sender) is { } item)
        {
            await ViewModel.DeleteAsync(item);
            UpdateStatus();
        }
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteAddedAsync();
        UpdateStatus();
    }

    /// <summary>The "…" menu of a download, built for that item when it opens.</summary>
    private void DownloadMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: TorrentDownloadItem item }
            || item.Download.ContentPath is not { } path)
        {
            return;
        }

        menu.Items.Clear();
        AddItem(menu, "Открыть", "", () => ViewModel.Open(item));
        AddItem(menu, "Открыть в новой вкладке", "", () => ViewModel.OpenInNewTab(item));
        if (item.IsFolder)
        {
            AddItem(menu, "Показать в папке", "", () => ViewModel.ShowInFolder(path));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Копировать путь", "", () => ViewModel.CopyPath(path));
    }

    private void TorrentMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: TorrentFileItem item })
        {
            return;
        }

        menu.Items.Clear();
        AddItem(menu, item.OpenText, "", () => ViewModel.OpenFile(item));
        AddItem(menu, "Показать в папке", "", () => ViewModel.ShowInFolder(item.File.Path));
        AddItem(menu, "Копировать путь", "", () => ViewModel.CopyPath(item.File.Path));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Удалить", "", async () =>
        {
            await ViewModel.DeleteAsync(item);
            UpdateStatus();
        });
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Games;
using Nexus.Core.Shell;
using Windows.Storage.Pickers;

namespace Nexus.App.Views;

public sealed partial class GamesPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();
    private bool _isActive;
    private bool _buildingFilter;

    public GamesPage()
    {
        ViewModel = App.Services.GetRequiredService<GamesViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        InitializeComponent();
    }

    public GamesViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = "Поиск игр…";
        _shell.SelectionText = string.Empty;
        await ViewModel.LoadAsync();
        UpdateStatus();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
    }

    public void Refresh() => _ = RefreshAsync();

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private async Task RefreshAsync()
    {
        await ViewModel.LoadAsync();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_isActive)
        {
            return;
        }

        _shell.StatusText = ViewModel.CountText;
        _shell.SelectionText = $"Занято на дисках: {ViewModel.TotalSizeText}";
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamesViewModel.Sources))
        {
            BuildSourceFilter();
        }
        else if (e.PropertyName == nameof(GamesViewModel.Games))
        {
            UpdateStatus();
        }
    }

    /// <summary>"Все" plus a tab for every launcher that actually has games.</summary>
    private void BuildSourceFilter()
    {
        var selected = SourceFilter.SelectedItem?.Tag as GameSource?;
        _buildingFilter = true;
        try
        {
            SourceFilter.Items.Clear();
            SourceFilter.Items.Add(new SelectorBarItem { Text = "Все", Tag = null });
            foreach (var source in ViewModel.Sources)
            {
                SourceFilter.Items.Add(new SelectorBarItem { Text = SourceTitle(source), Tag = source });
            }

            SourceFilter.SelectedItem = SourceFilter.Items.FirstOrDefault(item => Equals(item.Tag, selected)) ?? SourceFilter.Items[0];
        }
        finally
        {
            _buildingFilter = false;
        }
    }

    private static string SourceTitle(GameSource source) => source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Xbox => "Xbox",
        GameSource.Gog => "GOG",
        _ => "Локальные"
    };

    private void SourceFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_buildingFilter)
        {
            ViewModel.ApplyFilter(sender.SelectedItem?.Tag as GameSource?);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            await ViewModel.AddFolderAsync(folder.Path);
            UpdateStatus();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void MoreMenu_Opening(object sender, object e)
    {
        var hidden = ViewModel.HiddenCount;
        ShowHiddenItem.IsEnabled = hidden > 0;
        ShowHiddenItem.Text = hidden > 0 ? $"Показать скрытые игры ({hidden})" : "Скрытых игр нет";
    }

    private async void ShowHidden_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ShowHiddenAsync();
        UpdateStatus();
    }

    private void PlayFeatured_Click(object sender, RoutedEventArgs e) => ViewModel.Play(ViewModel.Featured);

    private void OpenFeaturedFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder(ViewModel.Featured);

    private void PlaySelected_Click(object sender, RoutedEventArgs e) => ViewModel.Play(ViewModel.SelectedGame);

    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder(ViewModel.SelectedGame);

    private void HideSelected_Click(object sender, RoutedEventArgs e) => ViewModel.Hide(ViewModel.SelectedGame);

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGame is { } game && Directory.Exists(game.InstallPath))
        {
            ShellLauncher.ShowInExplorer(game.InstallPath);
        }
    }

    private void GamesGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GameFromSource(e.OriginalSource) is { CanLaunch: true } game)
        {
            ViewModel.Play(game);
        }
    }

    private void GamesGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (GameFromSource(e.OriginalSource) is not { } game)
        {
            return;
        }

        ViewModel.SelectedGame = game;
        var menu = new MenuFlyout();
        AddItem(menu, "Играть", "", () => ViewModel.Play(game)).IsEnabled = game.CanLaunch;
        AddItem(menu, "Открыть папку игры", "", () => ViewModel.OpenFolder(game));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Скрыть из библиотеки", "", () => ViewModel.Hide(game));

        if (e.TryGetPosition(GamesGrid, out var point))
        {
            menu.ShowAt(GamesGrid, point);
        }
        else
        {
            menu.ShowAt(e.OriginalSource as FrameworkElement ?? GamesGrid);
        }

        e.Handled = true;
    }

    private GameItem? GameFromSource(object source)
    {
        for (var current = source as DependencyObject; current is not null && current != GamesGrid; current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorItem container)
            {
                return GamesGrid.ItemFromContainer(container) as GameItem;
            }
        }

        return null;
    }

    private static MenuFlyoutItem AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }
}

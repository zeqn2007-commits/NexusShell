using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Nexus.App.Views;

public sealed partial class RecycleBinPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();

    public RecycleBinPage()
    {
        ViewModel = App.Services.GetRequiredService<RecycleBinViewModel>();
        InitializeComponent();
    }

    public RecycleBinViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    public string SortGlyph(RecycleSortField field, bool descending, int column) =>
        (int)field == column ? (descending ? "" : "") : string.Empty;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shell.StatusText = string.Empty;
        _shell.SelectionText = string.Empty;
        await ViewModel.LoadAsync();
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is not (TextBox or AutoSuggestBox))
        {
            ItemsList.Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    public void Refresh() => _ = ViewModel.LoadAsync();

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    public void FocusContent() => ItemsList.Focus(FocusState.Keyboard);

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(ItemsList.SelectedItems.OfType<RecycleItemViewModel>().ToArray());

    private void Items_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not RecycleItemViewModel item)
        {
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, Items_ContainerContentChanging);
            return;
        }

        if (!item.IconRequested)
        {
            item.IconRequested = true;
            _ = LoadIconAsync(item);
        }
    }

    private async Task LoadIconAsync(RecycleItemViewModel item)
    {
        // The recycled copy keeps the original extension, so it gets the right type icon.
        var size = (int)Math.Ceiling(20 * (XamlRoot?.RasterizationScale ?? 1.0));
        var path = item.Entry.RecycledPath ?? item.Name;
        item.Icon = await _icons.GetIconAsync(path, item.Entry.IsFolder, item.Entry.IsFolder ? FileAttributes.Directory : FileAttributes.Normal, size);
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<RecycleSortField>(tag, out var field))
        {
            ViewModel.SortBy(field);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await ViewModel.RestoreSelectedAsync();

    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await ViewModel.RestoreAllAsync();

    private async void Delete_Click(object sender, RoutedEventArgs e) => await ViewModel.DeleteSelectedAsync();

    private async void Empty_Click(object sender, RoutedEventArgs e) => await ViewModel.EmptyAsync();

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or AutoSuggestBox)
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Delete:
                e.Handled = true;
                await ViewModel.DeleteSelectedAsync();
                break;
            case VirtualKey.A when ctrl:
                e.Handled = true;
                ItemsList.SelectAll();
                break;
        }
    }

    private void Items_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource);
        if (item is null)
        {
            return;
        }

        if (!ItemsList.SelectedItems.Contains(item))
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItems.Add(item);
        }

        var menu = new MenuFlyout();
        AddItem(menu, "Восстановить", "", () => _ = ViewModel.RestoreSelectedAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Удалить навсегда", "", () => _ = ViewModel.DeleteSelectedAsync());

        if (e.TryGetPosition(ItemsList, out var point))
        {
            menu.ShowAt(ItemsList, point);
        }
        else
        {
            menu.ShowAt(e.OriginalSource as FrameworkElement ?? ItemsList);
        }

        e.Handled = true;
    }

    private RecycleItemViewModel? ItemFromSource(object source)
    {
        for (var current = source as DependencyObject; current is not null && current != ItemsList; current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorItem container)
            {
                return ItemsList.ItemFromContainer(container) as RecycleItemViewModel;
            }
        }

        return null;
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

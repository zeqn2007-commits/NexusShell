using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
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
using Nexus.Core.IO;
using Nexus.Core.Settings;
using Nexus.Core.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Nexus.App.Views;

public sealed partial class FolderPage : Page, IShellPage, INotifyPropertyChanged
{
    private const string NexusPathsFormat = "Nexus.Paths";
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly PreviewService _previews = App.Services.GetRequiredService<PreviewService>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();
    private readonly ClassicMenuService _classicMenu = App.Services.GetRequiredService<ClassicMenuService>();
    private IReadOnlyList<string>? _dragPaths;
    private CancellationTokenSource? _previewCancellation;
    private FilePreview? _preview;
    private bool _suppressSelectionSync;
    private PointInt32 _menuPoint;

    public FolderPage()
    {
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.RenameRequested += (_, item) => FocusRenameBox(item);
        ViewModel.SelectRequested += (_, paths) => SelectPaths(paths);
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FolderViewModel ViewModel { get; }

    public bool SupportsSearch => true;

    public bool CanModifySelection => ViewModel.SelectedItems.Count > 0 && ViewModel.SelectedItems.All(item => !item.IsDrive);

    public bool SelectedHasLargeImage => ViewModel.SelectedItem?.HasLargeImage ?? false;

    /// <summary>The file's own picture/PDF page when available, otherwise the Windows thumbnail.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? PreviewImage => _preview?.Image ?? (HasPreviewText ? null : ViewModel.SelectedItem?.LargeImage);

    public string? PreviewText => _preview?.Text;

    public bool HasPreviewText => _preview?.Text is not null;

    public string? PreviewCaption => _preview?.Caption;

    public bool ShowIconPreview => PreviewImage is null && !HasPreviewText;

    public string SelectedSizeText => ViewModel.SelectedItem switch
    {
        null => string.Empty,
        { IsDrive: true } drive => drive.Location ?? string.Empty,
        { IsFolder: true } => "—",
        var file => $"{file.DisplaySize} ({file.Entry.Size:N0} байт)"
    };

    private ListViewBase ActiveList => ViewModel.IsTilesView ? FileGrid : FileList;

    public string SortGlyph(SortField field, bool descending, int column) =>
        (int)field == column ? (descending ? "\uE70D" : "\uE70E") : string.Empty;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NavLocation location)
        {
            await ViewModel.LoadAsync(location);
        }

        // Keyboard users land in the file list, like in Explorer (unless they are typing elsewhere).
        if (XamlRoot is null)
        {
            Loaded += FocusListOnce;
        }
        else
        {
            FocusList();
        }
    }

    private void FocusListOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= FocusListOnce;
        FocusList();
    }

    private void FocusList()
    {
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is not (TextBox or AutoSuggestBox))
        {
            ActiveList.Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    public void Refresh() => _ = ViewModel.ReloadAsync();

    public void OnSearchTextChanged(string text) => ViewModel.Filter(text);

    public void OnSearchSubmitted(string text) => _ = ViewModel.SearchAsync(text);

    public void FocusContent() => ActiveList.Focus(FocusState.Keyboard);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderViewModel.SelectedItem))
        {
            Notify(nameof(SelectedSizeText));
            Notify(nameof(SelectedHasLargeImage));
            LoadSelectedPreview();
            _ = LoadContentPreviewAsync(ViewModel.SelectedItem);
        }
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // ---------- Icons ----------

    private void Items_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItemViewModel item)
        {
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, Items_ContainerContentChanging);
            return;
        }

        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (sender == FileList)
        {
            if (item.TryBeginIconRequest())
            {
                _ = LoadIconAsync(item, (int)Math.Ceiling(20 * scale));
            }
        }
        else
        {
            var size = (int)Math.Ceiling(96 * scale);
            if (item.TryBeginLargeImageRequest(size))
            {
                _ = LoadLargeImageAsync(item, size);
            }
        }
    }

    private async Task LoadIconAsync(FileItemViewModel item, int size)
    {
        item.Icon = await _icons.GetIconAsync(item.Path, item.IsFolder, item.Entry.Attributes, Math.Max(size, 16));
    }

    private async Task LoadLargeImageAsync(FileItemViewModel item, int size)
    {
        var image = IconCache.SupportsThumbnail(item.Path) && !item.IsFolder
            ? await _icons.GetThumbnailAsync(item.Path, item.Entry.Modified, size)
                ?? await _icons.GetIconAsync(item.Path, item.IsFolder, item.Entry.Attributes, size)
            : await _icons.GetIconAsync(item.Path, item.IsFolder, item.Entry.Attributes, size);

        // A slower small request must not replace a sharper picture that arrived first.
        if (size < item.LargeImageSize && item.LargeImage is not null)
        {
            return;
        }

        item.LargeImage = image;
        if (item == ViewModel.SelectedItem)
        {
            Notify(nameof(SelectedHasLargeImage));
            NotifyPreview();
        }
    }

    private async Task LoadContentPreviewAsync(FileItemViewModel? item)
    {
        _previewCancellation?.Cancel();
        _preview = null;
        NotifyPreview();
        if (item is null || item.IsFolder || !ViewModel.IsDetailsPaneOpen || !PreviewService.CanPreview(item.Path))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var width = (int)Math.Ceiling(280 * (XamlRoot?.RasterizationScale ?? 1.0));
        var preview = await _previews.GetAsync(item.Path, item.Entry.Size, width, cancellation.Token);
        if (!cancellation.IsCancellationRequested && item == ViewModel.SelectedItem)
        {
            _preview = preview;
            NotifyPreview();
        }
    }

    private void NotifyPreview()
    {
        Notify(nameof(PreviewImage));
        Notify(nameof(PreviewText));
        Notify(nameof(HasPreviewText));
        Notify(nameof(PreviewCaption));
        Notify(nameof(ShowIconPreview));
    }

    private void LoadSelectedPreview()
    {
        var size = (int)Math.Ceiling(256 * (XamlRoot?.RasterizationScale ?? 1.0));
        if (ViewModel.SelectedItem is { } item && ViewModel.IsDetailsPaneOpen && item.TryBeginLargeImageRequest(size))
        {
            _ = LoadLargeImageAsync(item, size);
        }
    }

    // ---------- Selection ----------

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync || sender is not ListViewBase list || list != ActiveList)
        {
            return;
        }

        ViewModel.SetSelection(list.SelectedItems.OfType<FileItemViewModel>().ToArray());
        Notify(nameof(CanModifySelection));
    }

    private void SelectPaths(IReadOnlyList<string> paths)
    {
        var list = ActiveList;
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = ViewModel.Items.Where(item => wanted.Contains(item.Path)).ToArray();
        _suppressSelectionSync = true;
        try
        {
            list.SelectedItems.Clear();
            foreach (var item in items)
            {
                list.SelectedItems.Add(item);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        ViewModel.SetSelection(items);
        Notify(nameof(CanModifySelection));
        if (items.Length > 0)
        {
            list.ScrollIntoView(items[0]);
        }
    }

    private void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel { IsRenaming: false } item)
        {
            _ = ViewModel.OpenAsync(item);
            e.Handled = true;
        }
    }

    // ---------- Keyboard ----------

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or AutoSuggestBox or PasswordBox)
        {
            return;
        }

        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Enter when alt:
                ViewModel.ShowProperties();
                break;
            case VirtualKey.Enter:
                await ViewModel.OpenAsync();
                break;
            case VirtualKey.Delete:
                await ViewModel.DeleteAsync(permanently: shift);
                break;
            case VirtualKey.F2:
                ViewModel.BeginRename();
                break;
            case VirtualKey.C when ctrl && shift:
                ViewModel.CopyPath();
                break;
            case VirtualKey.C when ctrl:
                ViewModel.SetClipboard(ClipboardEffect.Copy);
                break;
            case VirtualKey.X when ctrl:
                ViewModel.SetClipboard(ClipboardEffect.Move);
                break;
            case VirtualKey.V when ctrl:
                await ViewModel.PasteAsync();
                break;
            case VirtualKey.Z when ctrl:
                await ViewModel.UndoAsync();
                break;
            case VirtualKey.N when ctrl && shift:
                await ViewModel.CreateFolderAsync();
                break;
            case VirtualKey.A when ctrl:
                ActiveList.SelectAll();
                break;
            case VirtualKey.Back:
                _shell.SelectedTab?.GoBack();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---------- Inline rename ----------

    private void FocusRenameBox(FileItemViewModel item)
    {
        var list = ActiveList;
        list.ScrollIntoView(item);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (list.ContainerFromItem(item) is not SelectorItem container
                || FindChild<TextBox>(container, "RenameBox") is not { } box)
            {
                item.IsRenaming = false;
                return;
            }

            box.Text = item.Name;
            box.Focus(FocusState.Programmatic);
            var stemLength = item.IsFolder || string.IsNullOrEmpty(item.Entry.Extension)
                ? item.Name.Length
                : item.Name.Length - item.Entry.Extension.Length;
            box.Select(0, stemLength);
        });
    }

    private async void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItemViewModel item } box)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(item, box.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelRename(item);
            ActiveList.Focus(FocusState.Programmatic);
        }
    }

    private async void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileItemViewModel { IsRenaming: true } item } box)
        {
            await CommitRenameAsync(item, box.Text);
        }
    }

    private async Task CommitRenameAsync(FileItemViewModel item, string text)
    {
        if (!item.IsRenaming)
        {
            return;
        }

        await ViewModel.CommitRenameAsync(item, text);
        ActiveList.Focus(FocusState.Programmatic);
    }

    private static T? FindChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------- Context menu (Windows 11 style: icon row + list) ----------

    private void Items_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListViewBase list)
        {
            return;
        }

        var item = ItemFromSource(list, e.OriginalSource);
        if (item is not null && !list.SelectedItems.Contains(item))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(item);
        }
        else if (item is null)
        {
            list.SelectedItems.Clear();
        }

        var pointer = e.TryGetPosition(list, out var point);
        _menuPoint = pointer ? WindowContext.CursorPosition : MenuAnchor(list, item);
        e.Handled = true;

        // Shift+right click and Shift+F10 go straight to the classic Windows menu, as in Explorer.
        if (IsDown(VirtualKey.Shift))
        {
            DispatcherQueue.TryEnqueue(() => ShowClassicMenu(background: item is null));
            return;
        }

        var flyout = item is null ? BuildBackgroundMenu() : BuildItemMenu(item);
        if (pointer)
        {
            flyout.ShowAt(list, new FlyoutShowOptions
            {
                Position = point,
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                ShowMode = FlyoutShowMode.Standard
            });
        }
        else
        {
            flyout.ShowAt(e.OriginalSource as FrameworkElement ?? list, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard });
        }
    }

    /// <summary>
    /// The item under the pointer, or the focused row when the menu key or Shift+F10 opened the menu
    /// (then the source is the container itself, which has no item DataContext of its own).
    /// </summary>
    private static FileItemViewModel? ItemFromSource(ListViewBase list, object source)
    {
        for (var current = source as DependencyObject; current is not null && current != list; current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorItem container)
            {
                return list.ItemFromContainer(container) as FileItemViewModel;
            }
        }

        return null;
    }

    /// <summary>Where a keyboard-opened menu appears: under the focused item, or at the top of the list.</summary>
    private PointInt32 MenuAnchor(ListViewBase list, FileItemViewModel? item) =>
        item is not null && list.ContainerFromItem(item) is FrameworkElement container
            ? _window.ToScreen(container, new Point(24, container.ActualHeight))
            : _window.ToScreen(list, new Point(24, 24));

    private CommandBarFlyout BuildItemMenu(FileItemViewModel item)
    {
        var flyout = new CommandBarFlyout { AlwaysExpanded = true };
        var several = ViewModel.SelectedItems.Count > 1;
        var editable = !item.IsDrive;

        flyout.PrimaryCommands.Add(IconCommand("Вырезать", "\uE8C6", () => ViewModel.SetClipboard(ClipboardEffect.Move), editable));
        flyout.PrimaryCommands.Add(IconCommand("Копировать", "\uE8C8", () => ViewModel.SetClipboard(ClipboardEffect.Copy), editable));
        flyout.PrimaryCommands.Add(IconCommand("Переименовать", "\uE8AC", () => ViewModel.BeginRename(), editable && !several));
        flyout.PrimaryCommands.Add(IconCommand("Удалить", "\uE74D", () => _ = ViewModel.DeleteAsync(false), editable));

        flyout.SecondaryCommands.Add(MenuCommand("Открыть", "\uE8E5", () => _ = ViewModel.OpenAsync(item), "Enter", !several));
        if (item.IsFolder)
        {
            flyout.SecondaryCommands.Add(MenuCommand("Открыть в новой вкладке", "\uE7C4", () => ViewModel.OpenInNewTab(item), enabled: !several));
        }
        else
        {
            flyout.SecondaryCommands.Add(MenuCommand("Открыть с помощью…", "\uE7AC", ViewModel.OpenWith, enabled: !several));
        }

        flyout.SecondaryCommands.Add(new AppBarSeparator());
        var favorite = ViewModel.IsFavorite(item);
        flyout.SecondaryCommands.Add(MenuCommand(favorite ? "Удалить из избранного" : "Добавить в избранное",
            favorite ? "\uE735" : "\uE734", () => _ = ViewModel.ToggleFavoriteAsync(), enabled: !several && !item.IsDrive));
        if (item.IsFolder && !item.IsDrive)
        {
            flyout.SecondaryCommands.Add(MenuCommand("Закрепить на панели быстрого доступа", "\uE718", ViewModel.PinToQuickAccess, enabled: !several));
        }

        flyout.SecondaryCommands.Add(MenuCommand("Копировать путь", "\uE71B", ViewModel.CopyPath, "Ctrl+Shift+C"));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(MenuCommand("Показать в Проводнике", "\uEC50", ViewModel.ShowInExplorer, enabled: !several));
        if (item.IsFolder)
        {
            flyout.SecondaryCommands.Add(MenuCommand("Открыть в терминале", "\uE756", ViewModel.OpenTerminal, enabled: !several));
        }

        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(MenuCommand("Свойства", "\uE946", ViewModel.ShowProperties, "Alt+Enter", !several));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        var paths = ViewModel.SelectedItems.Select(selected => selected.Path).ToArray();
        flyout.SecondaryCommands.Add(MenuCommand("Показать дополнительные параметры", "\uE8A7",
            () => ShowClassicMenuLater(flyout, background: false), "Shift+F10", ShellContextMenu.CanShowFor(paths)));
        return flyout;
    }

    private CommandBarFlyout BuildBackgroundMenu()
    {
        var flyout = new CommandBarFlyout { AlwaysExpanded = true };
        var physical = ViewModel.IsPhysicalFolder;
        flyout.PrimaryCommands.Add(IconCommand("Вставить", "\uE77F", () => _ = ViewModel.PasteAsync(), ViewModel.CanPaste));
        flyout.PrimaryCommands.Add(IconCommand("Новая папка", "\uE8F4", () => _ = ViewModel.CreateFolderAsync(), physical));
        flyout.PrimaryCommands.Add(IconCommand("Обновить", "\uE72C", Refresh, true));

        var view = new MenuFlyout();
        view.Items.Add(MenuItem("Крупные значки", "\uE80A", () => ViewModel.ViewMode = FolderViewMode.Tiles));
        view.Items.Add(MenuItem("Таблица", "\uE8FD", () => ViewModel.ViewMode = FolderViewMode.Details));
        flyout.SecondaryCommands.Add(new AppBarButton { Label = "Вид", Icon = new FontIcon { Glyph = "\uE8FD" }, Flyout = view });

        var sort = new MenuFlyout();
        sort.Items.Add(MenuItem("Имя", "\uE8C1", () => ViewModel.SetSortField(SortField.Name)));
        sort.Items.Add(MenuItem("Дата изменения", "\uE787", () => ViewModel.SetSortField(SortField.Modified)));
        sort.Items.Add(MenuItem("Тип", "\uE7C3", () => ViewModel.SetSortField(SortField.Type)));
        sort.Items.Add(MenuItem("Размер", "\uE9F9", () => ViewModel.SetSortField(SortField.Size)));
        flyout.SecondaryCommands.Add(new AppBarButton { Label = "Сортировка", Icon = new FontIcon { Glyph = "\uE8CB" }, Flyout = sort });

        flyout.SecondaryCommands.Add(new AppBarSeparator());
        var create = new MenuFlyout();
        create.Items.Add(MenuItem("Папку", "\uE8F4", () => _ = ViewModel.CreateFolderAsync()));
        create.Items.Add(MenuItem("Текстовый документ", "\uE7C3", () => _ = ViewModel.CreateTextFileAsync()));
        flyout.SecondaryCommands.Add(new AppBarButton { Label = "Создать", Icon = new FontIcon { Glyph = "\uE710" }, Flyout = create, IsEnabled = physical });
        flyout.SecondaryCommands.Add(MenuCommand("Отменить", "\uE7A7", () => _ = ViewModel.UndoAsync(), "Ctrl+Z"));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(MenuCommand("Открыть в терминале", "\uE756", ViewModel.OpenTerminal, enabled: physical));
        flyout.SecondaryCommands.Add(MenuCommand("Открыть в Проводнике", "\uEC50", ViewModel.ShowInExplorer, enabled: physical));
        flyout.SecondaryCommands.Add(MenuCommand("Свойства", "\uE946", ViewModel.ShowProperties, "Alt+Enter", physical));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(MenuCommand("Показать дополнительные параметры", "\uE8A7",
            () => ShowClassicMenuLater(flyout, background: true), "Shift+F10", physical));
        return flyout;
    }

    // ---------- Classic Windows menu ----------

    private void ShowClassicMenuLater(CommandBarFlyout flyout, bool background)
    {
        // The flyout hands keyboard focus back to the list when it finishes closing. Opening the
        // Windows menu only after that keeps the focus from jumping away (e.g. out of inline rename).
        if (!flyout.IsOpen)
        {
            DispatcherQueue.TryEnqueue(() => ShowClassicMenu(background));
            return;
        }

        flyout.Closed += (_, _) => DispatcherQueue.TryEnqueue(() => ShowClassicMenu(background));
        flyout.Hide();
    }

    /// <summary>The Windows menu with every shell extension (archivers, editors, antivirus, Git…).</summary>
    private void ShowClassicMenu(bool background)
    {
        if (background)
        {
            if (ViewModel.CurrentFolder is { } folder)
            {
                _classicMenu.ShowForFolder(this, folder, _menuPoint, verb => TryHandleShellVerb(verb, background: true));
            }
        }
        else if (ViewModel.SelectedItems.Count > 0)
        {
            var paths = ViewModel.SelectedItems.Select(item => item.Path).ToArray();
            _classicMenu.ShowForItems(this, paths, _menuPoint, verb => TryHandleShellVerb(verb, background: false));
        }
    }

    /// <summary>
    /// Verbs of the Windows menu that Nexus carries out itself, so undo, inline rename, tabs and
    /// the cut highlight keep working. Everything else (archivers, "Отправить", "Свойства"…) runs in Windows.
    /// </summary>
    private bool TryHandleShellVerb(string verb, bool background)
    {
        var single = ViewModel.SelectedItems.Count == 1 ? ViewModel.SelectedItems[0] : null;
        switch (verb.ToLowerInvariant())
        {
            case "open" when single is not null:
                _ = ViewModel.OpenAsync(single);
                return true;
            case "opennewtab" or "opennewwindow" when single is { IsFolder: true }:
                ViewModel.OpenInNewTab(single);
                return true;
            case "rename" when single is not null:
                ViewModel.BeginRename();
                return true;
            case "delete" when !background:
                _ = ViewModel.DeleteAsync(permanently: IsDown(VirtualKey.Shift));
                return true;
            case "cut" when !background:
                ViewModel.SetClipboard(ClipboardEffect.Move);
                return true;
            case "copy" when !background:
                ViewModel.SetClipboard(ClipboardEffect.Copy);
                return true;
            case "paste" when background:
                _ = ViewModel.PasteAsync();
                return true;
            case "newfolder" when background:
                _ = ViewModel.CreateFolderAsync();
                return true;
            case "refresh" when background:
                Refresh();
                return true;
            // Inside Nexus, "pin" and "add to favourites" go to Nexus's own quick access and favourites.
            case "pintohome" when single is { IsFolder: true, IsDrive: false }:
                ViewModel.PinToQuickAccess();
                return true;
            case "pintohomefile" when single is { IsFolder: false }:
                if (!ViewModel.IsFavorite(single))
                {
                    _ = ViewModel.ToggleFavoriteAsync();
                }

                return true;
            default:
                return false;
        }
    }

    private static AppBarButton IconCommand(string label, string glyph, Action action, bool enabled)
    {
        // Icon-only row with tooltips, as in the Windows 11 Explorer context menu.
        var button = new AppBarButton
        {
            Label = label,
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = enabled,
            LabelPosition = CommandBarLabelPosition.Collapsed
        };
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static AppBarButton MenuCommand(string label, string glyph, Action action, string? shortcut = null, bool enabled = true)
    {
        var button = new AppBarButton
        {
            Label = label,
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = enabled,
            KeyboardAcceleratorTextOverride = shortcut ?? string.Empty
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- Drag and drop ----------

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileItemViewModel>().Where(item => !item.IsDrive).ToArray();
        if (items.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        var paths = items.Select(item => item.Path).ToArray();
        e.Data.SetData(NexusPathsFormat, string.Join('|', paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storageItems = new List<IStorageItem>();
                foreach (var item in items)
                {
                    storageItems.Add(item.IsFolder
                        ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                        : await StorageFile.GetFileFromPathAsync(item.Path));
                }

                request.SetData(storageItems);
            }
            catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                request.SetData(Array.Empty<IStorageItem>());
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private async void Page_DragEnter(object sender, DragEventArgs e)
    {
        _dragPaths = null;
        var deferral = e.GetDeferral();
        try
        {
            _dragPaths = await ReadDragPathsAsync(e.DataView);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        var target = GetDropTarget(e);
        if (target is null || !(e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(NexusPathsFormat)))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var move = ShouldMove(e.Modifiers, _dragPaths, target);
        var paths = _dragPaths ?? [];
        if (paths.Any(path => PathHelper.AreEqual(path, target) || PathHelper.IsInside(target, path))
            || (move && paths.Count > 0 && paths.All(path => PathHelper.GetParent(path) is { } parent && PathHelper.AreEqual(parent, target))))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        var name = PathHelper.AreEqual(target, ViewModel.CurrentFolder ?? string.Empty)
            ? ViewModel.Location.Title
            : Path.GetFileName(target);
        e.DragUIOverride.Caption = move ? $"Переместить в «{name}»" : $"Копировать в «{name}»";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        var target = GetDropTarget(e);
        if (target is null)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var paths = _dragPaths ?? await ReadDragPathsAsync(e.DataView);
            if (paths is { Count: > 0 })
            {
                await ViewModel.TransferAsync(paths, target, ShouldMove(e.Modifiers, paths, target));
            }
        }
        finally
        {
            deferral.Complete();
            _dragPaths = null;
        }
    }

    private string? GetDropTarget(DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItemViewModel { IsFolder: true } folder)
        {
            return folder.Path;
        }

        return ViewModel.CurrentFolder;
    }

    /// <summary>Explorer's rule: same drive moves, another drive copies; Shift forces move, Ctrl forces copy.</summary>
    private static bool ShouldMove(DragDropModifiers modifiers, IReadOnlyList<string>? paths, string target)
    {
        if (modifiers.HasFlag(DragDropModifiers.Shift))
        {
            return true;
        }

        if (modifiers.HasFlag(DragDropModifiers.Control) || paths is null || paths.Count == 0)
        {
            return false;
        }

        var targetRoot = Path.GetPathRoot(target);
        return paths.All(path => string.Equals(Path.GetPathRoot(path), targetRoot, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<string>?> ReadDragPathsAsync(DataPackageView view)
    {
        try
        {
            if (view.Contains(NexusPathsFormat) && await view.GetDataAsync(NexusPathsFormat) is string text)
            {
                return text.Split('|', StringSplitOptions.RemoveEmptyEntries);
            }

            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                return items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToArray();
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    // ---------- Command bar ----------

    private void NewFolder_Click(object sender, RoutedEventArgs e) => _ = ViewModel.CreateFolderAsync();

    private void NewTextFile_Click(object sender, RoutedEventArgs e) => _ = ViewModel.CreateTextFileAsync();

    private void Cut_Click(object sender, RoutedEventArgs e) => ViewModel.SetClipboard(ClipboardEffect.Move);

    private void Copy_Click(object sender, RoutedEventArgs e) => ViewModel.SetClipboard(ClipboardEffect.Copy);

    private void Paste_Click(object sender, RoutedEventArgs e) => _ = ViewModel.PasteAsync();

    private void Rename_Click(object sender, RoutedEventArgs e) => ViewModel.BeginRename();

    private void CopyPath_Click(object sender, RoutedEventArgs e) => ViewModel.CopyPath();

    private void Delete_Click(object sender, RoutedEventArgs e) => _ = ViewModel.DeleteAsync(permanently: false);

    private void Open_Click(object sender, RoutedEventArgs e) => _ = ViewModel.OpenAsync();

    private void Explorer_Click(object sender, RoutedEventArgs e) => ViewModel.ShowInExplorer();

    private void Properties_Click(object sender, RoutedEventArgs e) => ViewModel.ShowProperties();

    private void Terminal_Click(object sender, RoutedEventArgs e) => ViewModel.OpenTerminal();

    private void PinFolder_Click(object sender, RoutedEventArgs e) => ViewModel.PinToQuickAccess();

    private void Undo_Click(object sender, RoutedEventArgs e) => _ = ViewModel.UndoAsync();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ActiveList.SelectAll();

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
        {
            ViewModel.SetSort(field);
        }
    }

    private void SortMenu_Opening(object sender, object e)
    {
        SortByName.IsChecked = ViewModel.SortField == SortField.Name;
        SortByModified.IsChecked = ViewModel.SortField == SortField.Modified;
        SortByType.IsChecked = ViewModel.SortField == SortField.Type;
        SortBySize.IsChecked = ViewModel.SortField == SortField.Size;
        SortAscending.IsChecked = !ViewModel.SortDescending;
        SortDescending.IsChecked = ViewModel.SortDescending;
    }

    private void SortField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
        {
            ViewModel.SetSortField(field);
        }
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetSortDirection((sender as FrameworkElement)?.Tag as string == "Descending");

    private void ViewMenu_Opening(object sender, object e)
    {
        ViewTiles.IsChecked = ViewModel.IsTilesView;
        ViewDetails.IsChecked = ViewModel.IsDetailsView;
        ShowHiddenToggle.IsChecked = ViewModel.ShowHiddenItems;
        ShowExtensionsToggle.IsChecked = ViewModel.ShowFileExtensions;
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = (sender as FrameworkElement)?.Tag as string == "Tiles" ? FolderViewMode.Tiles : FolderViewMode.Details;
        SelectPaths(ViewModel.SelectedItems.Select(item => item.Path).ToArray());
    }

    private void ShowHidden_Click(object sender, RoutedEventArgs e) => _ = ViewModel.SetShowHiddenAsync(ShowHiddenToggle.IsChecked);

    private void ShowExtensions_Click(object sender, RoutedEventArgs e) => _ = ViewModel.SetShowExtensionsAsync(ShowExtensionsToggle.IsChecked);
}

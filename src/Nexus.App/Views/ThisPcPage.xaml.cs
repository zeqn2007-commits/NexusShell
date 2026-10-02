using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.IO;
using Nexus.Core.Shell;
using Windows.Foundation;
using Windows.Graphics;

namespace Nexus.App.Views;

public sealed partial class ThisPcPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();
    private readonly ClassicMenuService _classicMenu = App.Services.GetRequiredService<ClassicMenuService>();
    private bool _isActive;

    public ThisPcPage()
    {
        ViewModel = App.Services.GetRequiredService<ThisPcViewModel>();
        InitializeComponent();
    }

    public ThisPcViewModel ViewModel { get; }

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

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private async Task LoadAsync()
    {
        await ViewModel.LoadAsync();
        if (!_isActive)
        {
            return;
        }

        _shell.StatusText = Formatting.Count(ViewModel.Drives.Count, "диск", "диска", "дисков");
        var size = (int)Math.Ceiling(48 * (XamlRoot?.RasterizationScale ?? 1.0));
        foreach (var drive in ViewModel.Drives.ToArray())
        {
            drive.Icon = await _icons.GetIconAsync(drive.RootPath, true, FileAttributes.Directory, size);
        }
    }

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DriveItem drive })
        {
            _shell.Navigate(NavLocation.ForFolder(drive.RootPath));
        }
    }

    private void Location_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NetworkLocation location })
        {
            return;
        }

        if (location.IsFolder)
        {
            _shell.Navigate(NavLocation.ForFolder(location.Target));
            return;
        }

        try
        {
            ShellLauncher.Open(location.Target);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            _shell.NotifyError($"Windows не смогла открыть «{location.Name}»: {exception.Message}");
        }
    }

    private void Drive_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DriveItem drive } element)
        {
            return;
        }

        e.Handled = true;
        var pointer = e.TryGetPosition(element, out var position);
        var point = pointer ? WindowContext.CursorPosition : _window.ToScreen(element, new Point(24, element.ActualHeight));

        // Shift+right click and Shift+F10 open the Windows menu straight away, as in Explorer.
        if (ClassicMenuService.IsShiftDown)
        {
            DispatcherQueue.TryEnqueue(() => ShowClassicMenu(drive, point));
            return;
        }

        var menu = new MenuFlyout();
        AddItem(menu, "Открыть", "", () => _shell.Navigate(NavLocation.ForFolder(drive.RootPath)));
        AddItem(menu, "Открыть в новой вкладке", "", () => _shell.OpenInNewTab(NavLocation.ForFolder(drive.RootPath)));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Свойства", "", () => ShowProperties(drive));
        menu.Items.Add(new MenuFlyoutSeparator());
        var more = new MenuFlyoutItem
        {
            Text = "Показать дополнительные параметры",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Shift+F10"
        };

        // Open the Windows menu only once this one has closed and handed focus back.
        more.Click += (_, _) => menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(() => ShowClassicMenu(drive, point));
        menu.Items.Add(more);

        if (pointer)
        {
            menu.ShowAt(element, position);
        }
        else
        {
            menu.ShowAt(element);
        }
    }

    /// <summary>The drive's Windows menu: eject, BitLocker, format and other drive commands.</summary>
    private void ShowClassicMenu(DriveItem drive, PointInt32 point) =>
        _classicMenu.ShowForItems(this, [drive.RootPath], point, verb =>
        {
            switch (verb.ToLowerInvariant())
            {
                case "open" or "explore":
                    _shell.Navigate(NavLocation.ForFolder(drive.RootPath));
                    return true;
                case "opennewtab" or "opennewwindow":
                    _shell.OpenInNewTab(NavLocation.ForFolder(drive.RootPath));
                    return true;
                default:
                    return false;
            }
        });

    private void ShowProperties(DriveItem drive)
    {
        try
        {
            ShellLauncher.ShowProperties(drive.RootPath, _window.Handle);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException)
        {
            _shell.NotifyError(exception.Message, "Не удалось открыть свойства");
        }
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Shell;
using Windows.Foundation;
using Windows.Graphics;

namespace Nexus.App.Views;

public sealed partial class AppsPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly IconCache _icons = App.Services.GetRequiredService<IconCache>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();
    private readonly ClassicMenuService _classicMenu = App.Services.GetRequiredService<ClassicMenuService>();
    private bool _isActive;

    public AppsPage()
    {
        ViewModel = App.Services.GetRequiredService<AppsViewModel>();
        InitializeComponent();
    }

    public AppsViewModel ViewModel { get; }

    public bool SupportsSearch => true;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = string.Empty;
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
        ViewModel.ApplyFilter(text);
        UpdateStatus();
    }

    /// <summary>Enter in the search box starts the app when only one matches, like Start's search.</summary>
    public void OnSearchSubmitted(string text)
    {
        ViewModel.ApplyFilter(text);
        if (ViewModel.Visible is [var only])
        {
            Launch(only);
        }
    }

    private async Task RefreshAsync()
    {
        await ViewModel.LoadAsync();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_isActive)
        {
            _shell.StatusText = ViewModel.CountText;
        }
    }

    private void Apps_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement { Tag: AppItemViewModel { IconRequested: false } app })
        {
            app.IconRequested = true;
            _ = LoadIconAsync(app);
        }
    }

    private async Task LoadIconAsync(AppItemViewModel app)
    {
        var size = (int)Math.Ceiling(32 * (XamlRoot?.RasterizationScale ?? 1.0));
        app.Icon = await _icons.GetAppIconAsync(app.App.ShellPath, size);
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppItemViewModel app })
        {
            Launch(app);
        }
    }

    private void Launch(AppItemViewModel app)
    {
        try
        {
            ShellLauncher.OpenUri(app.App.ShellPath);
        }
        catch (Win32Exception exception)
        {
            _shell.NotifyError($"Windows не смогла запустить «{app.Name}»: {exception.Message}");
        }
    }

    private void App_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppItemViewModel app } element)
        {
            return;
        }

        e.Handled = true;
        var pointer = e.TryGetPosition(element, out var position);
        var point = pointer ? WindowContext.CursorPosition : _window.ToScreen(element, new Point(24, element.ActualHeight));
        if (ClassicMenuService.IsShiftDown)
        {
            DispatcherQueue.TryEnqueue(() => ShowClassicMenu(app, point));
            return;
        }

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Открыть", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => Launch(app);
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        var more = new MenuFlyoutItem
        {
            Text = "Показать дополнительные параметры",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Shift+F10"
        };

        // Windows' own menu: run as administrator, open file location, uninstall, pin to Start.
        more.Click += (_, _) => menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(() => ShowClassicMenu(app, point));
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

    private void ShowClassicMenu(AppItemViewModel app, PointInt32 point) =>
        _classicMenu.ShowForItems(this, [app.App.ShellPath], point, _ => false);
}

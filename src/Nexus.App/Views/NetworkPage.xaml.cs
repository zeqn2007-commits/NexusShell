using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Shell;

namespace Nexus.App.Views;

public sealed partial class NetworkPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private CancellationTokenSource? _scan;

    public NetworkPage()
    {
        ViewModel = App.Services.GetRequiredService<NetworkViewModel>();
        InitializeComponent();
    }

    public NetworkViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shell.StatusText = string.Empty;
        _shell.SelectionText = string.Empty;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _scan?.Cancel();
    }

    public void Refresh() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        _scan?.Cancel();
        var scan = new CancellationTokenSource();
        _scan = scan;
        _shell.StatusText = "Поиск компьютеров в сети…";
        try
        {
            await ViewModel.LoadAsync(scan.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!scan.IsCancellationRequested)
        {
            _shell.StatusText = ViewModel.Computers.Count == 0
                ? string.Empty
                : Formatting.Count(ViewModel.Computers.Count, "компьютер", "компьютера", "компьютеров");
        }
    }

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NetworkTile tile })
        {
            return;
        }

        if (tile.IsFolder)
        {
            _shell.Navigate(NavLocation.ForFolder(tile.Target));
            return;
        }

        try
        {
            ShellLauncher.Open(tile.Target);
        }
        catch (Win32Exception exception)
        {
            _shell.NotifyError($"Windows не смогла открыть «{tile.Name}»: {exception.Message}");
        }
    }

    private void SharingSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellLauncher.OpenSharingSettings();
        }
        catch (Win32Exception exception)
        {
            _shell.NotifyError(exception.Message, "Не удалось открыть параметры");
        }
    }
}

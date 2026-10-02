using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Windows.Storage.Pickers;

namespace Nexus.App.Views;

public sealed partial class SettingsPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public string VersionText => $"Версия {ViewModel.Version} · файловый менеджер для Windows 11";

    public bool SupportsSearch => false;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shell.StatusText = string.Empty;
        _shell.SelectionText = string.Empty;
    }

    public void Refresh()
    {
    }

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.DataFolder);
        _shell.Navigate(NavLocation.ForFolder(ViewModel.DataFolder));
    }

    private async void Reset_Click(object sender, RoutedEventArgs e) => await ViewModel.ResetAsync();

    private async void AddGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
        {
            ViewModel.AddGameFolder(folder);
        }
    }

    private void RemoveGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            ViewModel.RemoveGameFolder(folder);
        }
    }

    private void ShowHiddenGames_Click(object sender, RoutedEventArgs e) => ViewModel.ShowHiddenGames();

    private async void AddAiFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } folder)
        {
            ViewModel.AddAiFolder(folder);
        }
    }

    private void RemoveAiFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            ViewModel.RemoveAiFolder(folder);
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}

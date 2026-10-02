using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Shell;

namespace Nexus.App.Views;

public sealed partial class SettingsPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();

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
}

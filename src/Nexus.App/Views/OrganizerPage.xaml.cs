using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Shell;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public sealed partial class OrganizerPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private bool _isActive;

    public OrganizerPage()
    {
        ViewModel = App.Services.GetRequiredService<OrganizerViewModel>();
        InitializeComponent();
    }

    public OrganizerViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = "Разбираю «Загрузки»…";
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

    private void OpenDownloads_Click(object sender, RoutedEventArgs e) => ViewModel.OpenDownloads();

    private async void SortAll_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SortAllAsync();
        UpdateStatus();
    }

    private async void SortGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<CategoryGroup>(sender) is { } group)
        {
            await ViewModel.SortAsync(group);
            UpdateStatus();
        }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<CleanupGroup>(sender) is { } group)
        {
            await ViewModel.DeleteGroupAsync(group);
            UpdateStatus();
        }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<CleanupItem>(sender) is { } item)
        {
            await ViewModel.DeleteAsync(item);
            UpdateStatus();
        }
    }

    private void ShowCleanupItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<CleanupItem>(sender) is { } item)
        {
            ViewModel.ShowInFolder(item.Path);
        }
    }
}

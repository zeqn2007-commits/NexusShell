using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Windows.Storage.Pickers;

namespace Nexus.App.Views;

public sealed partial class AiCenterPage : Page, IShellPage
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly WindowContext _window = App.Services.GetRequiredService<WindowContext>();
    private bool _isActive;

    public AiCenterPage()
    {
        ViewModel = App.Services.GetRequiredService<AiCenterViewModel>();
        InitializeComponent();
    }

    public AiCenterViewModel ViewModel { get; }

    public bool SupportsSearch => false;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _shell.StatusText = "Сканирование…";
        _shell.SelectionText = string.Empty;
        await ViewModel.LoadAsync(force: false);
        UpdateStatus();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
    }

    public void Refresh() => _ = ScanAsync();

    public void OnSearchTextChanged(string text)
    {
    }

    public void OnSearchSubmitted(string text)
    {
    }

    private async Task ScanAsync()
    {
        if (_isActive)
        {
            _shell.StatusText = "Сканирование…";
        }

        await ViewModel.LoadAsync(force: true);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_isActive)
        {
            _shell.StatusText = ViewModel.StatusText;
        }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ToggleSkills_Click(object sender, RoutedEventArgs e) => ViewModel.ShowAllSkills = !ViewModel.ShowAllSkills;

    private static T? ItemOf<T>(object sender)
        where T : class => (sender as FrameworkElement)?.Tag as T;

    private async void InstallSkill_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<AiFileItem>(sender) is { } item)
        {
            await ViewModel.InstallSkillAsync(item);
            UpdateStatus();
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<AiFileItem>(sender) is { } item)
        {
            ViewModel.Open(item);
        }
    }

    /// <summary>The "…" menu of a found AI file, built for that item when it opens.</summary>
    private void FileMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: AiFileItem item })
        {
            return;
        }

        menu.Items.Clear();
        AddItem(menu, item.File.IsFolder ? "Открыть папку" : "Открыть", "", () => ViewModel.Open(item));
        AddItem(menu, "Показать в папке", "", () => ViewModel.ShowInFolder(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.IsSkill)
        {
            AddItem(menu, "Установить для Claude Code", "", () => _ = ViewModel.InstallSkillAsync(item));
        }

        AddItem(menu, "Переложить в «Документы › AI»", "", () => _ = ViewModel.PutAwayAsync(item));
    }

    private void FoldersMenu_Opening(object sender, object e)
    {
        FoldersMenu.Items.Clear();
        AddItem(FoldersMenu, "Добавить папку…", "", () => _ = AddFolderAsync());
        var folders = ViewModel.ExtraFolders;
        if (folders.Count > 0)
        {
            FoldersMenu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (var folder in folders)
        {
            var entry = new MenuFlyoutSubItem { Text = Path.GetFileName(folder.TrimEnd('\\')), Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(entry, folder);
            var open = new MenuFlyoutItem { Text = "Открыть", Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => _shell.Navigate(NavLocation.ForFolder(folder));
            var remove = new MenuFlyoutItem { Text = "Убрать из поиска", Icon = new FontIcon { Glyph = "" } };
            remove.Click += async (_, _) => await ViewModel.RemoveFolderAsync(folder);
            entry.Items.Add(open);
            entry.Items.Add(remove);
            FoldersMenu.Items.Add(entry);
        }

        FoldersMenu.Items.Add(new MenuFlyoutSeparator());
        FoldersMenu.Items.Add(new MenuFlyoutItem
        {
            Text = "Всегда: «Загрузки», рабочий стол, «Документы»",
            IsEnabled = false
        });
    }

    private async Task AddFolderAsync()
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

    private void OpenModelFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<AiModelItem>(sender) is { } model && Directory.Exists(model.Model.Location))
        {
            _shell.Navigate(NavLocation.ForFolder(model.Model.Location));
        }
    }

    private void Skill_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<AiSkillItem>(sender) is { } skill && Directory.Exists(skill.Skill.Folder))
        {
            _shell.RequestSelection(Path.Combine(skill.Skill.Folder, Nexus.Core.Ai.SkillLibrary.SkillFileName));
            _shell.Navigate(NavLocation.ForFolder(skill.Skill.Folder));
        }
    }

    private void Project_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<AiProjectItem>(sender) is { } project && Directory.Exists(project.Path))
        {
            _shell.Navigate(NavLocation.ForFolder(project.Path));
        }
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}

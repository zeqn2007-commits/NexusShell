using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.Ai;
using Nexus.Core.Games;
using Nexus.Core.Integration;
using Nexus.Core.Settings;

namespace Nexus.App.ViewModels;

/// <summary>"Настройки": every choice is written to settings.json the moment it changes.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> SidebarTitles = new()
    {
        ["apps"] = "Приложения",
        ["games"] = "Игры",
        ["ai"] = "AI-центр",
        ["torrents"] = "Торренты"
    };

    private static readonly Dictionary<string, string> HomeTitles = new()
    {
        ["games"] = "Продолжить играть",
        ["files"] = "Недавние и избранное",
        ["drives"] = "Накопители",
        ["ai"] = "AI-центр"
    };

    private readonly SettingsStore _settings;
    private readonly DefaultFileManager _defaultFileManager;
    private readonly DialogService _dialogs;
    private readonly ShellViewModel _shell;
    private readonly GameLibrary _games;
    private readonly AiWorkspace _ai;
    private bool _updating;

    public SettingsViewModel(
        SettingsStore settings,
        DefaultFileManager defaultFileManager,
        DialogService dialogs,
        ShellViewModel shell,
        GameLibrary games,
        AiWorkspace ai)
    {
        _settings = settings;
        _defaultFileManager = defaultFileManager;
        _dialogs = dialogs;
        _shell = shell;
        _games = games;
        _ai = ai;
        Load();
    }

    [ObservableProperty]
    public partial bool OpensFolders { get; set; }

    [ObservableProperty]
    public partial int StartupIndex { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransparent))]
    public partial int MaterialIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransparencyText))]
    public partial double Transparency { get; set; }

    [ObservableProperty]
    public partial int ViewModeIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowDetailsPane { get; set; }

    [ObservableProperty]
    public partial bool SidebarApps { get; set; }

    [ObservableProperty]
    public partial bool SidebarGames { get; set; }

    [ObservableProperty]
    public partial bool SidebarAi { get; set; }

    [ObservableProperty]
    public partial bool SidebarTorrents { get; set; }

    [ObservableProperty]
    public partial bool ShowHiddenItems { get; set; }

    [ObservableProperty]
    public partial bool ShowFileExtensions { get; set; }

    [ObservableProperty]
    public partial int SortFieldIndex { get; set; }

    [ObservableProperty]
    public partial int SortDirectionIndex { get; set; }

    [ObservableProperty]
    public partial bool ConfirmRecycle { get; set; }

    [ObservableProperty]
    public partial bool ClassicContextMenu { get; set; }

    [ObservableProperty]
    public partial bool HomeGames { get; set; }

    [ObservableProperty]
    public partial bool HomeFiles { get; set; }

    [ObservableProperty]
    public partial bool HomeDrives { get; set; }

    [ObservableProperty]
    public partial bool HomeAi { get; set; }

    [ObservableProperty]
    public partial bool ShowSystemApps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GamesSummary))]
    public partial IReadOnlyList<string> GameFolders { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GamesSummary), nameof(HasHiddenGames), nameof(HiddenGamesText))]
    public partial int HiddenGamesCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiSummary))]
    public partial IReadOnlyList<string> AiFolders { get; private set; } = [];

    public bool IsTransparent => MaterialIndex != (int)WindowMaterial.Standard;

    public string TransparencyText => $"{Transparency:0} %";

    public string SidebarSummary => Summary(_settings.Current.HiddenSidebarSections, SidebarTitles);

    public string HomeSummary => Summary(_settings.Current.HiddenHomeSections, HomeTitles);

    public string GamesSummary
    {
        get
        {
            var folders = GameFolders.Count == 0
                ? "Steam, Epic, Xbox и GOG; своих папок нет"
                : $"Steam, Epic, Xbox, GOG и {Formatting.Count(GameFolders.Count, "папка", "папки", "папок")}";
            return HiddenGamesCount == 0 ? folders : $"{folders} · {Formatting.Count(HiddenGamesCount, "скрытая игра", "скрытые игры", "скрытых игр")}";
        }
    }

    public bool HasHiddenGames => HiddenGamesCount > 0;

    public string HiddenGamesText => $"Скрыто из библиотеки: {Formatting.Count(HiddenGamesCount, "игра", "игры", "игр")}";

    public string AiSummary => AiFolders.Count == 0
        ? "Ищет в «Загрузках», на рабочем столе и в «Документах»"
        : $"«Загрузки», рабочий стол, «Документы» и ещё {Formatting.Count(AiFolders.Count, "папка", "папки", "папок")}";

    public string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "—";

    public string DataFolder => SettingsStore.DataDirectory;

    public bool AddGameFolder(string folder)
    {
        if (!_games.AddFolder(folder))
        {
            _shell.Notify("Эта папка уже есть в библиотеке игр.");
            return false;
        }

        GameFolders = _games.Folders;
        return true;
    }

    public void RemoveGameFolder(string folder)
    {
        _games.RemoveFolder(folder);
        GameFolders = _games.Folders;
    }

    public void ShowHiddenGames()
    {
        var count = HiddenGamesCount;
        _games.ShowHidden();
        HiddenGamesCount = _games.HiddenCount;
        _shell.Notify($"В библиотеку вернулось: {Formatting.Count(count, "игра", "игры", "игр")}.", InfoBarSeverity.Success);
    }

    public bool AddAiFolder(string folder)
    {
        if (!_ai.AddFolder(folder))
        {
            _shell.Notify("Эта папка уже есть в поиске.");
            return false;
        }

        AiFolders = _ai.ExtraFolders;
        return true;
    }

    public void RemoveAiFolder(string folder)
    {
        _ai.RemoveFolder(folder);
        AiFolders = _ai.ExtraFolders;
    }

    public async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Сбросить настройки?",
                "Тема, вид папок, сортировка, переключатели и разделы вернутся к исходным. " +
                "Избранное, закреплённые и добавленные папки, скрытые игры и «открывать папки в Nexus» останутся как есть.",
                "Сбросить"))
        {
            return;
        }

        _settings.Update(settings => settings.ResetPreferences());
        Load();
        _shell.Notify("Настройки сброшены.", InfoBarSeverity.Success);
    }

    partial void OnStartupIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(StartupPage), value))
        {
            Save(settings => settings.StartupPage = (StartupPage)value);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(ThemePreference), value))
        {
            Save(settings => settings.Theme = (ThemePreference)value);
        }
    }

    partial void OnMaterialIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(WindowMaterial), value))
        {
            Save(settings => settings.Material = (WindowMaterial)value);
        }
    }

    partial void OnTransparencyChanged(double value) => Save(settings => settings.Transparency = (int)Math.Round(value));

    partial void OnViewModeIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(FolderViewMode), value))
        {
            Save(settings => settings.ViewMode = (FolderViewMode)value);
        }
    }

    partial void OnShowDetailsPaneChanged(bool value) => Save(settings => settings.ShowDetailsPane = value);

    partial void OnSidebarAppsChanged(bool value) => SetSidebar("apps", value);

    partial void OnSidebarGamesChanged(bool value) => SetSidebar("games", value);

    partial void OnSidebarAiChanged(bool value) => SetSidebar("ai", value);

    partial void OnSidebarTorrentsChanged(bool value) => SetSidebar("torrents", value);

    partial void OnShowHiddenItemsChanged(bool value) => Save(settings => settings.ShowHiddenItems = value);

    partial void OnShowFileExtensionsChanged(bool value) => Save(settings => settings.ShowFileExtensions = value);

    partial void OnSortFieldIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(SortField), value))
        {
            Save(settings => settings.SortField = (SortField)value);
        }
    }

    partial void OnSortDirectionIndexChanged(int value)
    {
        if (value is 0 or 1)
        {
            Save(settings => settings.SortDescending = value == 1);
        }
    }

    partial void OnConfirmRecycleChanged(bool value) => Save(settings => settings.ConfirmRecycle = value);

    partial void OnClassicContextMenuChanged(bool value) => Save(settings => settings.ClassicContextMenu = value);

    partial void OnHomeGamesChanged(bool value) => SetHome("games", value);

    partial void OnHomeFilesChanged(bool value) => SetHome("files", value);

    partial void OnHomeDrivesChanged(bool value) => SetHome("drives", value);

    partial void OnHomeAiChanged(bool value) => SetHome("ai", value);

    partial void OnShowSystemAppsChanged(bool value) => Save(settings => settings.ShowSystemApps = value);

    partial void OnOpensFoldersChanged(bool value)
    {
        if (!_updating)
        {
            _ = ApplyDefaultFileManagerAsync(value);
        }
    }

    /// <summary>Reads every value from the settings without writing anything back.</summary>
    private void Load()
    {
        var current = _settings.Current;
        _updating = true;
        OpensFolders = SafeIsEnabled();
        StartupIndex = (int)current.StartupPage;
        ThemeIndex = (int)current.Theme;
        MaterialIndex = (int)current.Material;
        Transparency = current.Transparency;
        ViewModeIndex = (int)current.ViewMode;
        ShowDetailsPane = current.ShowDetailsPane;
        SidebarApps = !current.HiddenSidebarSections.Contains("apps");
        SidebarGames = !current.HiddenSidebarSections.Contains("games");
        SidebarAi = !current.HiddenSidebarSections.Contains("ai");
        SidebarTorrents = !current.HiddenSidebarSections.Contains("torrents");
        ShowHiddenItems = current.ShowHiddenItems;
        ShowFileExtensions = current.ShowFileExtensions;
        SortFieldIndex = (int)current.SortField;
        SortDirectionIndex = current.SortDescending ? 1 : 0;
        ConfirmRecycle = current.ConfirmRecycle;
        ClassicContextMenu = current.ClassicContextMenu;
        HomeGames = !current.HiddenHomeSections.Contains("games");
        HomeFiles = !current.HiddenHomeSections.Contains("files");
        HomeDrives = !current.HiddenHomeSections.Contains("drives");
        HomeAi = !current.HiddenHomeSections.Contains("ai");
        ShowSystemApps = current.ShowSystemApps;
        GameFolders = _games.Folders;
        HiddenGamesCount = _games.HiddenCount;
        AiFolders = _ai.ExtraFolders;
        _updating = false;
        OnPropertyChanged(nameof(SidebarSummary));
        OnPropertyChanged(nameof(HomeSummary));
    }

    private void Save(Action<AppSettings> change)
    {
        if (!_updating && !_settings.Update(change))
        {
            _shell.NotifyError($"Не удалось записать настройки в {SettingsStore.DataDirectory}.");
        }
    }

    private void SetSidebar(string tag, bool visible)
    {
        Save(settings => Toggle(settings.HiddenSidebarSections, tag, visible));
        OnPropertyChanged(nameof(SidebarSummary));
    }

    private void SetHome(string tag, bool visible)
    {
        Save(settings => Toggle(settings.HiddenHomeSections, tag, visible));
        OnPropertyChanged(nameof(HomeSummary));
    }

    private static void Toggle(List<string> hidden, string tag, bool visible)
    {
        hidden.RemoveAll(entry => entry == tag);
        if (!visible)
        {
            hidden.Add(tag);
        }
    }

    private static string Summary(IEnumerable<string> hidden, Dictionary<string, string> titles)
    {
        var names = titles.Where(pair => hidden.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        return names.Length == 0 ? "Показаны все" : $"Скрыто: {string.Join(", ", names)}";
    }

    private async Task ApplyDefaultFileManagerAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                var confirmed = await _dialogs.ConfirmAsync(
                    "Открывать папки в Nexus?",
                    "Папки, диски, Win+E и ярлык Проводника будут открываться в Nexus. Панель задач, «Пуск» и рабочий стол " +
                    "останутся системными. Изменение касается только вашей учётной записи, его можно отменить здесь же в любой момент.",
                    "Открывать в Nexus");
                if (!confirmed || Environment.ProcessPath is not { } executable)
                {
                    SetSilently(false);
                    return;
                }

                _defaultFileManager.Enable(executable);
                _shell.Notify("Теперь папки открываются в Nexus.", InfoBarSeverity.Success);
            }
            else
            {
                _defaultFileManager.Disable();
                _shell.Notify("Папки снова открываются в Проводнике.", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetSilently(SafeIsEnabled());
            _shell.NotifyError(exception.Message, "Не удалось изменить настройку");
        }
    }

    private void SetSilently(bool value)
    {
        _updating = true;
        OpensFolders = value;
        _updating = false;
    }

    private bool SafeIsEnabled()
    {
        try
        {
            return _defaultFileManager.IsEnabled();
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}

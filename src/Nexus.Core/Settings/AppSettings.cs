using System.Text.Json.Serialization;

namespace Nexus.Core.Settings;

public enum SortField
{
    Name,
    Modified,
    Type,
    Size
}

public enum FolderViewMode
{
    Details,
    Tiles
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>What the window is made of.</summary>
public enum WindowMaterial
{
    /// <summary>As File Explorer: Mica behind the tabs, solid toolbar and workspace.</summary>
    Standard,

    /// <summary>Mica through the whole window, tinted by the wallpaper.</summary>
    Mica,

    /// <summary>Acrylic through the whole window: blurred desktop and windows show through.</summary>
    Glass
}

/// <summary>What a plain launch of Nexus opens (a folder or section asked for on the command line always wins).</summary>
public enum StartupPage
{
    Home,
    LastSession,
    ThisPc,
    Downloads
}

/// <summary>The window's last normal (not maximized) bounds in physical pixels, and whether it was maximized.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsMaximized { get; set; }
}

/// <summary>Everything Nexus remembers between launches.</summary>
public sealed class AppSettings
{
    /// <summary>Sidebar sections that can be turned off, by their tags.</summary>
    public static readonly IReadOnlyList<string> OptionalSidebarSections = ["apps", "games", "ai", "torrents"];

    /// <summary>Home page blocks that can be turned off: last games, recent and favourite files, drives, the AI summary.</summary>
    public static readonly IReadOnlyList<string> OptionalHomeSections = ["games", "files", "drives", "ai"];

    public int Version { get; set; } = 1;

    public bool ShowHiddenItems { get; set; }

    public bool ShowFileExtensions { get; set; } = true;

    public bool ShowDetailsPane { get; set; } = true;

    public FolderViewMode ViewMode { get; set; } = FolderViewMode.Details;

    public SortField SortField { get; set; } = SortField.Name;

    public bool SortDescending { get; set; }

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public WindowMaterial Material { get; set; } = WindowMaterial.Standard;

    /// <summary>0–100: how much of the backdrop shows through with <see cref="WindowMaterial.Mica"/> or <see cref="WindowMaterial.Glass"/>.</summary>
    public int Transparency { get; set; } = 50;

    /// <summary>Files and folders starred by the user.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Folders pinned to quick access in addition to the Windows known folders.</summary>
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>Extra folders scanned for portable/repack games.</summary>
    public List<string> GameFolders { get; set; } = [];

    /// <summary>Install folders the user hid from the game library.</summary>
    public List<string> HiddenGames { get; set; } = [];

    /// <summary>Extra roots scanned for AI projects.</summary>
    public List<string> AiFolders { get; set; } = [];

    public bool ShowSystemApps { get; set; }

    public StartupPage StartupPage { get; set; } = StartupPage.Home;

    /// <summary>Locations of the tabs open when Nexus was last closed, for <see cref="StartupPage.LastSession"/>.</summary>
    public List<string> LastSessionTabs { get; set; } = [];

    public int LastSessionSelectedTab { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Tags from <see cref="OptionalSidebarSections"/> the user turned off.</summary>
    public List<string> HiddenSidebarSections { get; set; } = [];

    /// <summary>Tags from <see cref="OptionalHomeSections"/> the user turned off.</summary>
    public List<string> HiddenHomeSections { get; set; } = [];

    /// <summary>Ask before sending items to the Recycle Bin (it can be undone either way).</summary>
    public bool ConfirmRecycle { get; set; }

    /// <summary>Right click opens the classic Windows context menu straight away instead of the Nexus one.</summary>
    public bool ClassicContextMenu { get; set; }

    /// <summary>
    /// "Сбросить настройки": appearance, behaviour and layout go back to the defaults; what the user collected —
    /// favourites, pinned and added folders, hidden games — and the last session stay.
    /// </summary>
    public void ResetPreferences()
    {
        var defaults = new AppSettings();
        ShowHiddenItems = defaults.ShowHiddenItems;
        ShowFileExtensions = defaults.ShowFileExtensions;
        ShowDetailsPane = defaults.ShowDetailsPane;
        ViewMode = defaults.ViewMode;
        SortField = defaults.SortField;
        SortDescending = defaults.SortDescending;
        Theme = defaults.Theme;
        Material = defaults.Material;
        Transparency = defaults.Transparency;
        ShowSystemApps = defaults.ShowSystemApps;
        StartupPage = defaults.StartupPage;
        HiddenSidebarSections = [];
        HiddenHomeSections = [];
        ConfirmRecycle = defaults.ConfirmRecycle;
        ClassicContextMenu = defaults.ClassicContextMenu;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

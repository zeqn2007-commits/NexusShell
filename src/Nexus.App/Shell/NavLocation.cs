namespace Nexus.App.Shell;

public enum PageKind
{
    Home,
    Folder,
    Recent,
    Favorites,
    ThisPc,
    Network,
    Apps,
    Games,
    AiCenter,
    Torrents,
    RecycleBin,
    Settings
}

/// <summary>Where a tab currently is: a section of Nexus or a physical folder.</summary>
public sealed record NavLocation(PageKind Kind, string Title, string Glyph, string? Path = null)
{
    public static NavLocation Home { get; } = new(PageKind.Home, "Главная", "\uE80F");

    public static NavLocation ForFolder(string path, string? title = null)
    {
        if (Nexus.Core.IO.PathHelper.IsNetworkComputer(path))
        {
            var computer = path.Trim('\\');
            return new NavLocation(PageKind.Folder, title ?? computer, "\uE7F8", $@"\\{computer}");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) && Nexus.Core.IO.PathHelper.IsDriveRoot(path))
        {
            // A shared folder (\\server\share) is named after the share, as in Explorer.
            return new NavLocation(PageKind.Folder, title ?? System.IO.Path.GetFileName(path.TrimEnd('\\')), "\uE8CE", path.TrimEnd('\\'));
        }

        if (Nexus.Core.IO.PathHelper.IsDriveRoot(path))
        {
            return new NavLocation(PageKind.Folder, title ?? DriveTitle(path), "\uEDA2", path);
        }

        var name = title ?? KnownFolderTitle(path) ?? System.IO.Path.GetFileName(path.TrimEnd('\\'));
        return new NavLocation(PageKind.Folder, string.IsNullOrEmpty(name) ? path : name, "\uE8B7", path);
    }

    private static string DriveTitle(string root)
    {
        var drive = Nexus.Core.IO.Drives.GetReady().FirstOrDefault(entry =>
            string.Equals(entry.RootPath, root, StringComparison.OrdinalIgnoreCase));
        return drive?.DisplayName ?? root.TrimEnd('\\');
    }

    /// <summary>Russian display names for the Windows known folders ("Documents" → «Документы»).</summary>
    public static string? KnownFolderTitle(string path)
    {
        var normalized = path.TrimEnd('\\');
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string Path, string Title)[] known =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Рабочий стол"),
            (System.IO.Path.Combine(profile, "Downloads"), "Загрузки"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Документы"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Изображения"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Музыка"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Видео")
        ];
        return known.FirstOrDefault(item =>
            string.Equals(item.Path.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase)).Title;
    }

    public static NavLocation FromTag(string tag) => tag switch
    {
        "home" => Home,
        "recent" => new(PageKind.Recent, "Недавние", "\uE81C"),
        "favorites" => new(PageKind.Favorites, "Избранное", "\uE734"),
        "thispc" => new(PageKind.ThisPc, "Этот компьютер", "\uE7F8"),
        "network" => new(PageKind.Network, "Сеть", "\uEC27"),
        "apps" => new(PageKind.Apps, "Приложения", "\uE71D"),
        "games" => new(PageKind.Games, "Игры", "\uE7FC"),
        "ai" => new(PageKind.AiCenter, "AI-центр", "\uE794"),
        "torrents" => new(PageKind.Torrents, "Торренты", "\uE8F7"),
        "recycle" => new(PageKind.RecycleBin, "Корзина", "\uE74D"),
        "settings" => new(PageKind.Settings, "Настройки", "\uE713"),
        _ when tag.StartsWith("folder:", StringComparison.Ordinal) => ForFolder(tag["folder:".Length..]),
        _ => Home
    };

    /// <summary>Tag of the sidebar item that represents this location, if any.</summary>
    public string SidebarTag => Kind switch
    {
        PageKind.Home => "home",
        PageKind.Recent => "recent",
        PageKind.Favorites => "favorites",
        PageKind.ThisPc => "thispc",
        PageKind.Network => "network",
        PageKind.Apps => "apps",
        PageKind.Games => "games",
        PageKind.AiCenter => "ai",
        PageKind.Torrents => "torrents",
        PageKind.RecycleBin => "recycle",
        PageKind.Settings => "settings",
        _ when Path is not null && Path.StartsWith(@"\\", StringComparison.Ordinal) => "network",
        _ => $"folder:{Path}"
    };
}

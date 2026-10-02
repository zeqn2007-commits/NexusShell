using Microsoft.Win32;

namespace Nexus.Core.Games;

/// <summary>Installed Steam games: every library from libraryfolders.vdf and its appmanifest_*.acf files.</summary>
public static class SteamLibrary
{
    private const int FullyInstalled = 4;

    // Redistributables and runtimes Steam installs next to games.
    private static readonly HashSet<string> ToolAppIds = ["228980", "1070560", "1391110", "1628350", "1493710", "1826330"];

    private static readonly string[] CoverNames = ["library_600x900.jpg", "library_600x900.png", "library_capsule.jpg", "library_capsule.png"];
    private static readonly string[] HeroNames = ["library_hero.jpg", "library_hero.png"];

    public static string? FindRoot()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        var path = user?.GetValue("SteamPath") as string ?? machine?.GetValue("InstallPath") as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var root = Path.GetFullPath(path.Replace('/', '\\'));
        return Directory.Exists(root) ? root : null;
    }

    public static IReadOnlyList<GameEntry> Load(string steamRoot)
    {
        var games = new List<GameEntry>();
        foreach (var library in GetLibraries(steamRoot))
        {
            var steamApps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                if (ReadGame(steamRoot, steamApps, manifest) is { } game)
                {
                    games.Add(game);
                }
            }
        }

        // Steam also installs programs (Blender, Wallpaper Engine) and tools; only its app info knows which.
        var types = SteamAppInfo.ReadTypes(Path.Combine(steamRoot, "appcache", "appinfo.vdf"), games.Select(AppId));
        return games.Where(game => !types.TryGetValue(AppId(game), out var type) || !SteamAppInfo.IsNotAGame(type)).ToArray();
    }

    private static string AppId(GameEntry game) => game.Id["steam:".Length..];

    private static GameEntry? ReadGame(string steamRoot, string steamApps, string manifestPath)
    {
        var state = ValveKeyValues.TryLoad(manifestPath)?.Child("AppState");
        var appId = state?["appid"];
        var name = state?["name"];
        var installDir = state?["installdir"];
        if (appId is null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)
            || ToolAppIds.Contains(appId) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // StateFlags 4 means "fully installed"; downloads in progress are left out.
        if (int.TryParse(state!["StateFlags"], out var flags) && (flags & FullyInstalled) == 0)
        {
            return null;
        }

        return new GameEntry(
            $"steam:{appId}",
            name,
            GameSource.Steam,
            Path.Combine(steamApps, "common", installDir),
            $"steam://rungameid/{appId}",
            FindArtwork(steamRoot, appId, CoverNames),
            FindArtwork(steamRoot, appId, HeroNames),
            long.TryParse(state["SizeOnDisk"], out var size) && size > 0 ? size : null,
            long.TryParse(state["LastPlayed"], out var played) && played > 0 ? DateTimeOffset.FromUnixTimeSeconds(played) : null);
    }

    private static IEnumerable<string> GetLibraries(string steamRoot)
    {
        // libraryfolders.vdf keeps the real letter case; the registry stores the root in lower case.
        var libraries = new List<string>();
        var folders = ValveKeyValues.TryLoad(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"))?.Child("libraryfolders");
        foreach (var library in folders?.Children.Values ?? Enumerable.Empty<ValveKeyValues>())
        {
            if (library["path"] is { Length: > 0 } path && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(path);
            }
        }

        if (!libraries.Contains(steamRoot, StringComparer.OrdinalIgnoreCase))
        {
            libraries.Insert(0, steamRoot);
        }

        return libraries;
    }

    /// <summary>Steam's library cache: appcache\librarycache\{appid}\…, newer clients add a hash-named subfolder.</summary>
    private static string? FindArtwork(string steamRoot, string appId, IReadOnlyList<string> names)
    {
        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        var folder = Path.Combine(cache, appId);
        foreach (var name in names)
        {
            var direct = Path.Combine(folder, name);
            if (File.Exists(direct))
            {
                return direct;
            }

            var legacy = Path.Combine(cache, $"{appId}_{name}");
            if (File.Exists(legacy))
            {
                return legacy;
            }
        }

        if (!Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            var subfolders = Directory.GetDirectories(folder);
            return names
                .SelectMany(name => subfolders.Select(subfolder => Path.Combine(subfolder, name)))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

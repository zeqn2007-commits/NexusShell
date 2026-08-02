using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nexus.Core.Models;

namespace Nexus.Core.Services;

public sealed partial class GameLibraryService : IGameLibraryService
{
    private const int MinimumExecutableScore = 8;
    private static readonly HashSet<string> GenericLibraryNames = new(
        [
            "Games", "Game", "Игры", "Portable Games", "PortableGames",
            "Repack", "Repacks", "Torrents", "Torrent Games", "Торренты",
            "Downloads", "Загрузки", "GOG Games", "EA Games",
            "Origin Games", "Riot Games", "Rockstar Games",
            "Battle.net", "MY.GAMES", "Mail.Ru", "Gaijin"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] IgnoredExecutableFragments =
    [
        "unins", "uninstall", "setup", "install", "installer", "redist", "vcredist",
        "vc_redist", "unitycrashhandler", "crashreport", "crashpad",
        "updater", "updatehelper", "easyanticheat", "eac",
        "directx", "dxsetup", "dotnet", "prereq", "benchmark",
        "crashhandler", "crashsender", "crashuploader", "crashdump",
        "dedicatedserver", "serverbrowser", "configtool",
        "configurationtool", "leveleditor", "unlocker", "patcher",
        "keygen", "saveeditor", "modmanager", "trainer"
    ];
    private static readonly string[] DiscouragedExecutableFragments =
    [
        "launcher", "editor", "server", "config"
    ];
    private static readonly string[] IgnoredDirectoryNames =
    [
        "_CommonRedist", "Redist", "Redistributables", "Prerequisites",
        "Installer", "Installers", "Support", "CrashReportClient"
    ];
    private static readonly HashSet<string> LauncherOnlyApplicationNames = new(
        [
            "steam",
            "epicgameslauncher",
            "eaapp",
            "origin",
            "ubisoftconnect",
            "goggalaxy",
            "rockstargameslauncher",
            "battlenet",
            "riotclient",
            "mygamesgamecenter",
            "gamecenter",
            "gaijinnetagent"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> KnownLauncherExecutableNames = new(
        [
            "steam", "steamservice", "epicgameslauncher",
            "epicwebhelper", "eaapp", "eadesktop", "origin",
            "ubisoftconnect", "upc", "galaxyclient",
            "galaxyclienthelper", "battlenet", "riotclientservices",
            "riotclientux", "rockstargameslauncher",
            "socialclubhelper", "gamecenter", "gaijinnetagent",
            "launcher", "gamelauncher"
        ],
        StringComparer.Ordinal);
    private readonly IReadOnlyList<string>? _steamRoots;
    private readonly string? _epicManifestDirectory;
    private readonly IReadOnlyList<string>? _xboxRoots;
    private readonly IReadOnlyList<string>? _localGameRoots;
    private readonly IReadOnlyList<string>? _automaticGameRoots;
    private readonly IReadOnlyList<StartApplicationInfo>? _startApplications;
    private readonly string _localGameRootsPath;
    private readonly string _ignoredGamesPath;
    private readonly HashSet<string> _ignoredGamePaths;
    private readonly SemaphoreSlim _localGameRootsWriteLock = new(1, 1);
    private readonly SemaphoreSlim _ignoredGamesWriteLock = new(1, 1);

    public GameLibraryService(
        IEnumerable<string>? steamRoots = null,
        string? epicManifestDirectory = null,
        IEnumerable<string>? xboxRoots = null,
        IEnumerable<string>? localGameRoots = null,
        string? localGameRootsPath = null,
        string? ignoredGamesPath = null,
        IEnumerable<string>? automaticGameRoots = null,
        IEnumerable<StartApplicationInfo>? startApplications = null)
    {
        _steamRoots = steamRoots?.ToArray();
        _epicManifestDirectory = epicManifestDirectory;
        _xboxRoots = xboxRoots?.ToArray();
        _localGameRoots = localGameRoots?.ToArray();
        _automaticGameRoots = automaticGameRoots?.ToArray();
        _startApplications = startApplications?.ToArray();
        _localGameRootsPath = Path.GetFullPath(
            localGameRootsPath
            ?? LibraryScanRoots.DefaultLocalGameRootsPath);
        _ignoredGamesPath = Path.GetFullPath(
            ignoredGamesPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexus Shell",
                "ignored-games.json"));
        _ignoredGamePaths = LoadPathSet(_ignoredGamesPath);
    }

    public Task<IReadOnlyList<GameEntry>> GetInstalledGamesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var games = new List<GameEntry>();
            LoadSteamGames(games, cancellationToken);
            LoadEpicGames(games, cancellationToken);
            LoadXboxGames(games, cancellationToken);
            LoadStartMenuGames(games, cancellationToken);
            LoadTorrentGames(games, cancellationToken);
            LoadLocalGames(games, cancellationToken);

            return MergeEquivalentGames(games
                .Where(game => Directory.Exists(game.InstallPath))
                .Where(game => !IsIgnoredGame(game.InstallPath)))
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private bool IsIgnoredGame(string installPath)
    {
        var fullPath = Path.GetFullPath(installPath);
        lock (_ignoredGamePaths)
        {
            return _ignoredGamePaths.Contains(fullPath);
        }
    }

    public int HiddenGamesCount
    {
        get
        {
            lock (_ignoredGamePaths)
            {
                return _ignoredGamePaths.Count;
            }
        }
    }

    public async Task<bool> HideGameAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var fullPath = Path.GetFullPath(game.InstallPath);
        await _ignoredGamesWriteLock.WaitAsync(cancellationToken);
        try
        {
            bool changed;
            string[] snapshot;
            lock (_ignoredGamePaths)
            {
                changed = _ignoredGamePaths.Add(fullPath);
                snapshot = _ignoredGamePaths.ToArray();
            }

            if (!changed)
            {
                return false;
            }

            try
            {
                await SavePathSetAsync(
                    _ignoredGamesPath,
                    snapshot,
                    cancellationToken);
            }
            catch
            {
                lock (_ignoredGamePaths)
                {
                    _ignoredGamePaths.Remove(fullPath);
                }

                throw;
            }

            return true;
        }
        finally
        {
            _ignoredGamesWriteLock.Release();
        }
    }

    public async Task<int> RestoreHiddenGamesAsync(
        CancellationToken cancellationToken = default)
    {
        await _ignoredGamesWriteLock.WaitAsync(cancellationToken);
        try
        {
            string[] previous;
            lock (_ignoredGamePaths)
            {
                previous = _ignoredGamePaths.ToArray();
                _ignoredGamePaths.Clear();
            }

            if (previous.Length == 0)
            {
                return 0;
            }

            try
            {
                await SavePathSetAsync(
                    _ignoredGamesPath,
                    [],
                    cancellationToken);
            }
            catch
            {
                lock (_ignoredGamePaths)
                {
                    _ignoredGamePaths.UnionWith(previous);
                }

                throw;
            }

            return previous.Length;
        }
        finally
        {
            _ignoredGamesWriteLock.Release();
        }
    }

    public IReadOnlyList<string> GetLocalGameFolders()
    {
        return GetConfiguredLocalGameRoots()
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetAutomaticGameFolders()
    {
        return GetAutomaticLocalGameRoots()
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetScanRoots()
    {
        var useSystemDefaults =
            _localGameRoots is null
            && _automaticGameRoots is null;
        return LibraryScanRoots.GetDefaultRoots(
            GetConfiguredLocalGameRoots()
                .Concat(GetAutomaticLocalGameRoots()),
            _localGameRootsPath,
            includeFixedDrives: useSystemDefaults,
            includeProfileFolders: useSystemDefaults);
    }

    public async Task<bool> AddLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        await _localGameRootsWriteLock.WaitAsync(cancellationToken);
        try
        {
            var roots = LoadConfiguredLocalGameRoots().ToList();
            if (roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            roots.Add(fullPath);
            await SaveConfiguredLocalGameRootsAsync(roots, cancellationToken);
            return true;
        }
        finally
        {
            _localGameRootsWriteLock.Release();
        }
    }

    public async Task<bool> RemoveLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        await _localGameRootsWriteLock.WaitAsync(cancellationToken);
        try
        {
            var roots = LoadConfiguredLocalGameRoots().ToList();
            var removed = roots.RemoveAll(item =>
                string.Equals(
                    Path.GetFullPath(item),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
            {
                return false;
            }

            await SaveConfiguredLocalGameRootsAsync(roots, cancellationToken);
            return true;
        }
        finally
        {
            _localGameRootsWriteLock.Release();
        }
    }

    public void Launch(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var target = game.LaunchTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            OpenInstallFolder(game);
            return;
        }

        Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = Directory.Exists(game.InstallPath)
                ? game.InstallPath
                : string.Empty
        });
    }

    public void OpenInstallFolder(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Directory.Exists(game.InstallPath))
        {
            throw new DirectoryNotFoundException(
                $"Папка игры «{game.InstallPath}» больше недоступна.");
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.InstallPath}\"")
        {
            UseShellExecute = true
        });
    }

    private void LoadSteamGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        foreach (var steamRoot in GetSteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamAppsDirectories = GetSteamAppsDirectories(steamRoot);

            foreach (var steamAppsDirectory in steamAppsDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(steamAppsDirectory))
                {
                    continue;
                }

                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory.EnumerateFiles(
                        steamAppsDirectory,
                        "appmanifest_*.acf",
                        SearchOption.TopDirectoryOnly);
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var manifestPath in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = ReadValveKeyValues(manifestPath);
                    if (!values.TryGetValue("name", out var name)
                        || !values.TryGetValue("installdir", out var installDirectory)
                        || string.IsNullOrWhiteSpace(name)
                        || string.IsNullOrWhiteSpace(installDirectory))
                    {
                        continue;
                    }

                    values.TryGetValue("appid", out var appId);
                    if (IsSteamSystemComponent(appId, name))
                    {
                        continue;
                    }

                    var installPath = Path.Combine(
                        steamAppsDirectory,
                        "common",
                        installDirectory);
                    if (!Directory.Exists(installPath))
                    {
                        continue;
                    }

                    var gameExecutable = FindLikelyGameExecutable(
                        installPath,
                        cancellationToken);
                    games.Add(new GameEntry(
                        name,
                        installPath,
                        "Steam",
                        string.IsNullOrWhiteSpace(appId)
                            ? null
                            : $"steam://rungameid/{appId}",
                        appId,
                        ArtworkPath: FindSteamArtwork(steamRoot, appId)
                        ?? FindLocalArtwork(
                            installPath,
                            [appId, name, installDirectory],
                            allowGenericNames: false)
                        ?? TryGetExecutableIcon(gameExecutable),
                        DetectionDetail: "Найдена по манифесту Steam"));
                }
            }
        }
    }

    private void LoadEpicGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        var manifestDirectory = _epicManifestDirectory;
        if (manifestDirectory is null)
        {
            manifestDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic",
                "EpicGamesLauncher",
                "Data",
                "Manifests");
        }

        if (!Directory.Exists(manifestDirectory))
        {
            return;
        }

        IEnumerable<string> manifestPaths;
        try
        {
            manifestPaths = Directory.EnumerateFiles(
                manifestDirectory,
                "*.item",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var name = GetJsonString(root, "DisplayName");
                var installPath = GetJsonString(root, "InstallLocation");
                var appName = GetJsonString(root, "AppName");
                var catalogId = GetJsonString(root, "CatalogItemId");
                var catalogNamespace = GetJsonString(root, "CatalogNamespace");

                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(installPath)
                    || !Directory.Exists(installPath))
                {
                    continue;
                }

                var launchId = BuildEpicLaunchId(
                    catalogNamespace,
                    catalogId,
                    appName);
                var gameExecutable = FindLikelyGameExecutable(
                    installPath,
                    cancellationToken);
                games.Add(new GameEntry(
                    name,
                    installPath,
                    "Epic Games",
                        string.IsNullOrWhiteSpace(launchId)
                            ? null
                            : $"com.epicgames.launcher://apps/{launchId}?action=launch&silent=true",
                        launchId,
                        ArtworkPath: FindLocalArtwork(
                            installPath,
                            [appName, name, Path.GetFileNameWithoutExtension(gameExecutable)],
                            allowGenericNames: false)
                        ?? TryGetExecutableIcon(gameExecutable),
                        DetectionDetail: "Найдена по манифесту Epic Games"));
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
            }
        }
    }

    private void LoadXboxGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        foreach (var xboxRoot in GetXboxRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(xboxRoot))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(
                             xboxRoot,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(name)
                        || string.Equals(name, "Content", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var gameExecutable = FindLikelyGameExecutable(
                        directory,
                        cancellationToken);
                    games.Add(new GameEntry(
                        name,
                        directory,
                        "Xbox",
                        null,
                        AppId: BuildLocalExecutableIdentity(gameExecutable),
                        ArtworkPath: FindLocalArtwork(
                            directory,
                            [name, Path.GetFileNameWithoutExtension(gameExecutable)],
                            allowGenericNames: false)
                        ?? TryGetExecutableIcon(gameExecutable),
                        DetectionDetail: "Найдена в библиотеке Xbox"));
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private void LoadStartMenuGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        if (_startApplications is null
            && (_steamRoots is not null
                || _epicManifestDirectory is not null
                || _xboxRoots is not null
                || _localGameRoots is not null
                || _automaticGameRoots is not null))
        {
            // Explicit constructor roots define a closed scan universe for
            // deterministic tests and isolated consumers.
            return;
        }

        var applications = _startApplications
            ?? StartApplicationCatalog.EnumeratePhysical(
                StartApplicationCatalog.GetDefaultStartMenuRoots(),
                cancellationToken);
        foreach (var application in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LauncherOnlyApplicationNames.Contains(
                    NormalizeName(application.Name))
                || IsInfrastructureStartApplication(application))
            {
                continue;
            }

            var executable = application.ResolvedExecutablePath;
            if (!IsSafeShortcutExecutable(executable, application.Arguments))
            {
                continue;
            }

            var installPath = FindShortcutInstallRoot(
                executable!,
                application.WorkingDirectory,
                application.Name);
            if (installPath is null)
            {
                continue;
            }

            var likelyExecutable = FindLikelyGameExecutable(
                installPath,
                cancellationToken);
            if (likelyExecutable is null
                || !HasStrongShortcutGameSignature(
                    installPath,
                    likelyExecutable,
                    application.Name))
            {
                continue;
            }

            var launchTarget = !string.IsNullOrWhiteSpace(
                                   application.SourcePath)
                               && File.Exists(application.SourcePath)
                ? application.SourcePath
                : executable;
            games.Add(new GameEntry(
                application.Name,
                installPath,
                "Local",
                launchTarget,
                AppId: BuildLocalExecutableIdentity(likelyExecutable),
                ArtworkPath: TryGetShortcutIcon(application)
                ?? TryGetExistingArtwork(application.IconSourcePath)
                ?? TryGetExecutableIcon(likelyExecutable)
                ?? FindLocalArtwork(
                    installPath,
                    [application.Name, Path.GetFileNameWithoutExtension(likelyExecutable)],
                    allowGenericNames: false),
                DetectionDetail:
                    "Найдена по ярлыку меню «Пуск» и файлам игры",
                CanDeleteFiles: false));
        }
    }

    private static bool IsSafeShortcutExecutable(
        string? executable,
        string? arguments)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || !File.Exists(executable)
            || !Path.GetExtension(executable).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(executable);
            var windows = Environment.GetFolderPath(
                Environment.SpecialFolder.Windows);
            var windowsPrefix = string.IsNullOrWhiteSpace(windows)
                ? null
                : Path.GetFullPath(windows).TrimEnd('\\')
                    + Path.DirectorySeparatorChar;
            if (windowsPrefix is not null
                && fullPath.StartsWith(
                    windowsPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(arguments)
                || (!arguments.Contains(
                        "http://",
                        StringComparison.OrdinalIgnoreCase)
                    && !arguments.Contains(
                        "https://",
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsInfrastructureStartApplication(
        StartApplicationInfo application)
    {
        var normalizedName = NormalizeName(application.Name);
        if (LauncherOnlyApplicationNames.Contains(normalizedName)
            || IsInfrastructureApplicationLabel(normalizedName))
        {
            return true;
        }

        var executableStem = Path.GetFileNameWithoutExtension(
            application.ResolvedExecutablePath);
        var normalizedExecutable = NormalizeName(executableStem);
        return KnownLauncherExecutableNames.Contains(normalizedExecutable)
            || IsInfrastructureExecutableName(executableStem);
    }

    private static bool IsInfrastructureApplicationLabel(
        string normalizedName)
    {
        string[] fragments =
        [
            "uninstall", "uninstaller", "crashreport", "crashhandler",
            "crashsender", "crashuploader", "dedicatedserver",
            "configurationtool"
        ];
        return normalizedName.Equals("setup", StringComparison.Ordinal)
            || normalizedName.StartsWith("setupwizard", StringComparison.Ordinal)
            || fragments.Any(fragment => normalizedName.Contains(
                fragment,
                StringComparison.Ordinal));
    }

    private static string? TryGetShortcutIcon(
        StartApplicationInfo application)
    {
        var shortcut = application.SourcePath;
        if (string.IsNullOrWhiteSpace(shortcut)
            || !File.Exists(shortcut))
        {
            return null;
        }

        var extension = Path.GetExtension(shortcut);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(
                   ".appref-ms",
                   StringComparison.OrdinalIgnoreCase)
            ? ShellIconCache.TryGetIconPath(shortcut)
            : null;
    }

    private static string? FindShortcutInstallRoot(
        string executable,
        string? workingDirectory,
        string applicationName)
    {
        try
        {
            var executablePath = Path.GetFullPath(executable);
            var executableDirectory = Path.GetDirectoryName(executablePath);
            if (executableDirectory is null)
            {
                return null;
            }

            var initial = !string.IsNullOrWhiteSpace(workingDirectory)
                          && Directory.Exists(workingDirectory)
                          && IsPathInside(executablePath, workingDirectory)
                ? Path.GetFullPath(workingDirectory)
                : executableDirectory;
            var normalizedApplicationName = NormalizeName(applicationName);
            var current = new DirectoryInfo(initial);
            string? best = null;
            for (var depth = 0;
                 current is not null && depth <= 4;
                 depth++, current = current.Parent)
            {
                if (IsBroadInstallRoot(current.FullName))
                {
                    break;
                }

                var normalizedDirectoryName = NormalizeName(current.Name);
                if (normalizedApplicationName.Length >= 4
                    && normalizedDirectoryName.Length >= 4
                    && (normalizedApplicationName.Contains(
                            normalizedDirectoryName,
                            StringComparison.Ordinal)
                        || normalizedDirectoryName.Contains(
                            normalizedApplicationName,
                            StringComparison.Ordinal)))
                {
                    best = current.FullName;
                }

                if (HasKnownGamePathSegment(current.FullName))
                {
                    best ??= current.FullName;
                }
            }

            return best ?? initial;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasStrongShortcutGameSignature(
        string installPath,
        string executable,
        string applicationName)
    {
        var normalizedName = NormalizeName(applicationName);
        var normalizedFolder = NormalizeName(Path.GetFileName(
            installPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
        var normalizedExecutable = NormalizeName(
            Path.GetFileNameWithoutExtension(executable));
        var nameMatches = normalizedName.Length >= 4
            && ((normalizedFolder.Length >= 4
                 && (normalizedName.Contains(
                         normalizedFolder,
                         StringComparison.Ordinal)
                     || normalizedFolder.Contains(
                         normalizedName,
                         StringComparison.Ordinal)))
                || (normalizedExecutable.Length >= 4
                    && (normalizedName.Contains(
                            normalizedExecutable,
                            StringComparison.Ordinal)
                        || normalizedExecutable.Contains(
                            normalizedName,
                            StringComparison.Ordinal))));
        if (!nameMatches)
        {
            return false;
        }

        return HasGameRuntimeMarkers(installPath, executable)
            || HasKnownGamePathSegment(installPath);
    }

    private static bool HasKnownGamePathSegment(string path)
    {
        string[] knownSegments =
        [
            "Games", "Game", "Игры", "steamapps", "SteamLibrary",
            "GOG Games", "Epic Games", "EA Games", "Origin Games",
            "Riot Games", "Rockstar Games", "Ubisoft Game Launcher",
            "Battle.net", "MY.GAMES", "Mail.Ru", "Gaijin"
        ];
        return path
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => knownSegments.Contains(
                segment,
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsPathInside(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(
            fullDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExistingArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) && IsSupportedArtworkFile(path)
                ? Path.GetFullPath(path)
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private void LoadLocalGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        var configuredRoots = GetConfiguredLocalGameRoots()
            .Where(Directory.Exists)
            .ToArray();
        foreach (var root in configuredRoots)
        {
            ScanLocalGameRoot(
                games,
                root,
                canDeleteFiles: true,
                requireDirectGameSignature:
                    RequiresDirectGameSignature(root),
                cancellationToken);
        }

        var configuredSet = configuredRoots.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetAutomaticLocalGameRoots())
        {
            if (!configuredSet.Contains(root))
            {
                ScanLocalGameRoot(
                    games,
                    root,
                    canDeleteFiles: false,
                    requireDirectGameSignature:
                        RequiresDirectGameSignature(root),
                    cancellationToken);
            }
        }
    }

    private static void ScanLocalGameRoot(
        ICollection<GameEntry> games,
        string root,
        bool canDeleteFiles,
        bool requireDirectGameSignature,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
        {
            return;
        }

        var rootName = Path.GetFileName(
            root.TrimEnd(Path.DirectorySeparatorChar));
        if (!GenericLibraryNames.Contains(rootName)
            && LooksLikeGameInstallRoot(root))
        {
            TryAddLocalGame(
                games,
                root,
                "Local",
                null,
                canDeleteFiles,
                cancellationToken);
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(LibraryScanRoots.ShouldTraverse)
                .Take(300)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBroadInstallRoot(candidate))
            {
                ScanNestedGameFolders(
                    games,
                    candidate,
                    canDeleteFiles,
                    requireDirectGameSignature,
                    cancellationToken);
                continue;
            }

            if (requireDirectGameSignature
                && !LooksLikeGameInstallRoot(candidate))
            {
                continue;
            }

            TryAddLocalGame(
                games,
                candidate,
                "Local",
                null,
                canDeleteFiles,
                cancellationToken);
        }
    }

    private static void ScanNestedGameFolders(
        ICollection<GameEntry> games,
        string container,
        bool canDeleteFiles,
        bool requireDirectGameSignature,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> nestedCandidates;
        try
        {
            nestedCandidates = Directory
                .EnumerateDirectories(
                    container,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(LibraryScanRoots.ShouldTraverse)
                .Take(120)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var nestedCandidate in nestedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requireDirectGameSignature
                && !LooksLikeGameInstallRoot(nestedCandidate))
            {
                continue;
            }

            TryAddLocalGame(
                games,
                nestedCandidate,
                "Local",
                null,
                canDeleteFiles,
                cancellationToken);
        }
    }

    private void LoadTorrentGames(
        ICollection<GameEntry> games,
        CancellationToken cancellationToken)
    {
        foreach (var torrentPath in EnumerateTorrentFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentName = TorrentMetadataReader.TryReadContentName(torrentPath);
            var torrentName = Path.GetFileNameWithoutExtension(torrentPath);
            // Metadata describes the payload. The .torrent file name is only a
            // fallback because users and clients may rename it independently.
            var names = new[]
                {
                    IsSafeTorrentContentName(contentName)
                        ? contentName
                        : torrentName
                }
                .Where(IsSafeTorrentContentName)
                .Cast<string>()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (names.Length == 0)
            {
                continue;
            }

            foreach (var association in FindTorrentDataDirectories(
                         torrentPath,
                         names,
                         cancellationToken))
            {
                TryAddLocalGame(
                    games,
                    association.Path,
                    "Torrent",
                    torrentPath,
                    canDeleteFiles: false,
                    cancellationToken,
                    association.Detail);
            }
        }
    }

    private IEnumerable<string> EnumerateTorrentFiles(
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        foreach (var root in GetTorrentScanRoots())
        {
            pending.Enqueue((root, 0));
        }

        var visitedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        while (pending.Count > 0 && visited < 4000 && results.Count < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            if (!visitedPaths.Add(current))
            {
                continue;
            }

            visited++;
            try
            {
                foreach (var torrentPath in Directory.EnumerateFiles(
                             current,
                             "*.torrent",
                             SearchOption.TopDirectoryOnly))
                {
                    if (results.Count >= 500)
                    {
                        break;
                    }

                    results.Add(torrentPath);
                }

                if (depth >= 5)
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (LibraryScanRoots.ShouldTraverse(directory))
                    {
                        pending.Enqueue((directory, depth + 1));
                    }
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> GetTorrentScanRoots()
    {
        var roots = GetScanRoots().ToList();
        if (_localGameRoots is null && _automaticGameRoots is null)
        {
            roots.AddRange(
                LibraryScanRoots.GetKnownTorrentMetadataRoots());
        }

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<TorrentAssociation> FindTorrentDataDirectories(
        string torrentPath,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        var torrentDirectory = Path.GetDirectoryName(torrentPath);
        if (!string.IsNullOrWhiteSpace(torrentDirectory))
        {
            roots.Add(torrentDirectory);
        }

        roots.AddRange(GetConfiguredLocalGameRoots());
        roots.AddRange(GetAutomaticLocalGameRoots());
        var candidates = new List<TorrentDirectoryCandidate>();
        var normalizedTorrentDirectory = string.IsNullOrWhiteSpace(
            torrentDirectory)
            ? null
            : Path.GetFullPath(torrentDirectory);
        var fuzzyNames = names
            .SelectMany(BuildTorrentNameVariants)
            .Where(name => name.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var root in roots
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isTorrentDirectory = normalizedTorrentDirectory is not null
                && string.Equals(
                    Path.GetFullPath(root),
                    normalizedTorrentDirectory,
                    StringComparison.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var exactPath = TryGetSafeChildDirectory(root, name);
                if (exactPath is not null)
                {
                    candidates.Add(new TorrentDirectoryCandidate(
                        exactPath,
                        isTorrentDirectory ? 160 : 140,
                        MatchQuality: 100));
                }
            }

            var pending = new Queue<(string Path, int Depth)>();
            try
            {
                foreach (var directory in Directory
                             .EnumerateDirectories(
                                 root,
                                 "*",
                                 SearchOption.TopDirectoryOnly)
                             .Where(CanTraverseGameDirectory)
                             .Where(LibraryScanRoots.ShouldTraverse)
                             .Take(500))
                {
                    pending.Enqueue((directory, 1));
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0 && visited.Count < 1_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Dequeue();
                if (!visited.Add(directory))
                {
                    continue;
                }

                var directoryName = NormalizeName(
                    Path.GetFileName(directory));
                var matchQuality = GetTorrentNameMatchQuality(
                    directoryName,
                    fuzzyNames);
                if (matchQuality > 0)
                {
                    var proximityScore = isTorrentDirectory ? 80 : 60;
                    candidates.Add(new TorrentDirectoryCandidate(
                        directory,
                        proximityScore + matchQuality - (depth - 1) * 10,
                        matchQuality));
                }

                if (depth >= 2)
                {
                    continue;
                }

                try
                {
                    foreach (var nested in Directory
                                 .EnumerateDirectories(
                                     directory,
                                     "*",
                                     SearchOption.TopDirectoryOnly)
                                 .Where(CanTraverseGameDirectory)
                                 .Where(LibraryScanRoots.ShouldTraverse)
                                 .Take(250))
                    {
                        pending.Enqueue((nested, depth + 1));
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        var ranked = candidates
            .GroupBy(
                candidate => Path.GetFullPath(candidate.Path),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.MatchQuality)
                .First() with { Path = group.Key })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length == 0)
        {
            return [];
        }

        var winners = ranked
            .Where(candidate => candidate.Score == ranked[0].Score)
            .ToArray();
        if (winners.Length != 1)
        {
            return [];
        }

        var winner = winners[0];
        var runnerUpScore = ranked.Length > 1 ? ranked[1].Score : int.MinValue;
        if (winner.MatchQuality < 60
            && runnerUpScore != int.MinValue
            && winner.Score - runnerUpScore < 15)
        {
            return [];
        }

        var detail = winner.MatchQuality >= 60
            ? "Совпали имя .torrent и папка с игровыми файлами; состояние торрент-клиента не подтверждено"
            : "Вероятная связь по имени .torrent и игровым файлам; проверьте путь";
        return [new TorrentAssociation(winner.Path, detail)];
    }

    private static IEnumerable<string> BuildTorrentNameVariants(
        string value)
    {
        var normalized = NormalizeName(value);
        if (normalized.Length > 0)
        {
            yield return normalized;
        }

        var releaseSuffix = TorrentReleaseSuffixRegex()
            .Replace(value, " ")
            .Trim(' ', '-', '_', '.');
        var simplified = NormalizeName(releaseSuffix);
        if (simplified.Length > 0
            && !simplified.Equals(normalized, StringComparison.Ordinal))
        {
            yield return simplified;
        }
    }

    private static int GetTorrentNameMatchQuality(
        string directoryName,
        IReadOnlyList<string> names)
    {
        if (directoryName.Length < 4)
        {
            return 0;
        }

        var quality = 0;
        foreach (var name in names)
        {
            if (directoryName.Equals(name, StringComparison.Ordinal))
            {
                quality = Math.Max(quality, 60);
                continue;
            }

            var shorter = Math.Min(directoryName.Length, name.Length);
            var longer = Math.Max(directoryName.Length, name.Length);
            if (shorter >= 8
                && (directoryName.Contains(name, StringComparison.Ordinal)
                    || name.Contains(directoryName, StringComparison.Ordinal))
                && shorter / (double)longer >= 0.85)
            {
                quality = Math.Max(quality, 25);
            }
        }

        return quality;
    }

    private static bool LooksLikeGameInstallRoot(string path)
    {
        if (IsBroadInstallRoot(path))
        {
            return false;
        }

        try
        {
            var folderName = NormalizeName(Path.GetFileName(
                path.TrimEnd(Path.DirectorySeparatorChar)));
            foreach (var executable in Directory.EnumerateFiles(
                         path,
                         "*.exe",
                         SearchOption.TopDirectoryOnly))
            {
                var file = new FileInfo(executable);
                if (file.Length >= 64 * 1024
                    && ScoreExecutableCandidate(
                        path,
                        executable,
                        file.Length,
                        depth: 0,
                        folderName) >= MinimumExecutableScore)
                {
                    return true;
                }
            }

            return Directory.Exists(Path.Combine(path, "Binaries"))
                && (Directory.Exists(Path.Combine(path, "Content"))
                    || Directory.Exists(Path.Combine(path, "Engine")));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            return false;
        }
    }

    private static void TryAddLocalGame(
        ICollection<GameEntry> games,
        string installPath,
        string source,
        string? torrentPath,
        bool canDeleteFiles,
        CancellationToken cancellationToken,
        string? detectionDetail = null)
    {
        var executable = FindLikelyGameExecutable(
            installPath,
            cancellationToken);
        if (executable is null)
        {
            return;
        }

        var directoryName = Path.GetFileName(
            installPath.TrimEnd(Path.DirectorySeparatorChar));
        var productName = TryGetProductName(executable);
        var name = string.IsNullOrWhiteSpace(productName)
            || IsGenericProductName(productName)
            ? directoryName
            : productName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        games.Add(new GameEntry(
            name,
            installPath,
            source,
            executable,
            AppId: BuildLocalExecutableIdentity(executable),
            ArtworkPath: FindLocalArtwork(
                installPath,
                [name, directoryName, Path.GetFileNameWithoutExtension(executable)],
                allowGenericNames: true)
            ?? ShellIconCache.TryGetIconPath(executable),
            RelatedTorrentPath: torrentPath,
            DetectionDetail: detectionDetail ?? (source == "Torrent"
                ? "Связана по .torrent; проверьте путь"
                : canDeleteFiles
                    ? "Найдена в добавленной вами папке"
                    : "EXE и игровые файлы найдены в локальной папке; требуется проверка"),
            CanDeleteFiles: canDeleteFiles));
    }

    private static string? FindLikelyGameExecutable(
        string installPath,
        CancellationToken cancellationToken)
    {
        var folderName = NormalizeName(Path.GetFileName(
            installPath.TrimEnd(Path.DirectorySeparatorChar)));
        var candidates = new List<(string Path, int Score, long Size)>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((installPath, 0));
        var visitedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var visitedDirectories = 0;

        while (pending.Count > 0 && visitedDirectories < 180)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            string fullCurrent;
            try
            {
                fullCurrent = Path.GetFullPath(current);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                continue;
            }

            if (!visitedPaths.Add(fullCurrent))
            {
                continue;
            }

            visitedDirectories++;
            try
            {
                foreach (var executable in Directory.EnumerateFiles(
                             fullCurrent,
                             "*.exe",
                             SearchOption.TopDirectoryOnly))
                {
                    var stem = Path.GetFileNameWithoutExtension(executable);
                    if (IsInfrastructureExecutableName(stem))
                    {
                        continue;
                    }

                    var size = new FileInfo(executable).Length;
                    if (size < 64 * 1024)
                    {
                        continue;
                    }

                    var score = ScoreExecutableCandidate(
                        installPath,
                        executable,
                        size,
                        depth,
                        folderName);
                    if (score >= MinimumExecutableScore)
                    {
                        candidates.Add((executable, score, size));
                    }
                }

                if (depth >= 4)
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             fullCurrent,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!CanTraverseGameDirectory(directory))
                    {
                        continue;
                    }

                    pending.Enqueue((directory, depth + 1));
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Size)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static bool CanTraverseGameDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return !IgnoredDirectoryNames.Contains(
                       directory.Name,
                       StringComparer.OrdinalIgnoreCase)
                   && !directory.Attributes.HasFlag(
                       FileAttributes.ReparsePoint)
                   && !directory.Attributes.HasFlag(FileAttributes.System)
                   && !directory.Attributes.HasFlag(FileAttributes.Hidden);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static int ScoreExecutableCandidate(
        string installPath,
        string executable,
        long size,
        int depth,
        string folderName)
    {
        var stem = Path.GetFileNameWithoutExtension(executable);
        if (IsInfrastructureExecutableName(stem))
        {
            return int.MinValue;
        }

        var normalizedStem = NormalizeName(stem);
        if (KnownLauncherExecutableNames.Contains(normalizedStem))
        {
            return int.MinValue;
        }

        var score = Math.Max(0, 4 - depth);
        if (normalizedStem.Length >= 3
            && folderName.Length >= 3
            && (normalizedStem.Contains(folderName, StringComparison.Ordinal)
                || folderName.Contains(normalizedStem, StringComparison.Ordinal)))
        {
            score += 8;
        }

        var current = Path.GetDirectoryName(executable) ?? installPath;
        if (HasPathSegment(current, "Binaries"))
        {
            score += 4;
        }

        if (HasGameRuntimeMarkers(installPath, executable))
        {
            score += 5;
        }

        var productName = TryGetProductName(executable);
        if (!string.IsNullOrWhiteSpace(productName)
            && !IsGenericProductName(productName))
        {
            var normalizedProductName = NormalizeName(productName);
            score += normalizedProductName.Length >= 3
                && folderName.Length >= 3
                && (normalizedProductName.Contains(folderName, StringComparison.Ordinal)
                    || folderName.Contains(normalizedProductName, StringComparison.Ordinal))
                    ? 3
                    : 1;
        }

        if (size >= 5L * 1024 * 1024)
        {
            score += 2;
        }
        else if (size >= 1024 * 1024)
        {
            score++;
        }

        if (DiscouragedExecutableFragments.Any(fragment =>
                LooksLikeInfrastructureExecutable(stem, fragment)))
        {
            score -= 7;
        }

        return score;
    }

    private static bool HasGameRuntimeMarkers(
        string installPath,
        string executable)
    {
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? installPath;
        var executableStem = Path.GetFileNameWithoutExtension(executable);
        string[] roots =
        [
            installPath,
            executableDirectory
        ];
        string[] markerFiles =
        [
            "UnityPlayer.dll",
            "GameAssembly.dll",
            "steam_api.dll",
            "steam_api64.dll",
            "Galaxy.dll",
            "Galaxy64.dll",
            "EOSSDK-Win64-Shipping.dll",
            "data.win"
        ];

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(root =>
                markerFiles.Any(marker => File.Exists(Path.Combine(root, marker)))
                || HasFilePattern(root, "goggame-*.info")
                || HasFilePattern(root, "*.pck")
                || Directory.Exists(Path.Combine(root, $"{executableStem}_Data"))
                || Directory.Exists(Path.Combine(root, "MonoBleedingEdge"))
                || (Directory.Exists(Path.Combine(root, "Engine"))
                    && Directory.Exists(Path.Combine(root, "Content"))));
    }

    private static bool HasFilePattern(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(
                    root,
                    pattern,
                    SearchOption.TopDirectoryOnly)
                .Take(1)
                .Any();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasPathSegment(string path, string segment)
    {
        return path
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInfrastructureExecutable(
        string stem,
        string fragment)
    {
        if (stem.Equals(fragment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] separatedForms =
        [
            $"_{fragment}",
            $"-{fragment}",
            $" {fragment}"
        ];
        if (separatedForms.Any(form =>
                stem.Contains(form, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var pascalSuffix = char.ToUpperInvariant(fragment[0])
            + fragment[1..];
        return stem.EndsWith(pascalSuffix, StringComparison.Ordinal);
    }

    private static bool IsInfrastructureExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(value);
        var normalizedStem = NormalizeName(stem);
        string[] hardPrefixes =
        [
            "unins", "uninstall", "setup", "vcredist",
            "dxsetup", "unitycrashhandler", "crashreport",
            "crashhandler", "crashsender", "crashuploader",
            "crashdump", "easyanticheat", "dedicatedserver"
        ];
        if (hardPrefixes.Any(prefix => normalizedStem.StartsWith(
                prefix,
                StringComparison.Ordinal)))
        {
            return true;
        }

        string[] strongSubstrings =
        [
            "unitycrashhandler", "crashreport", "crashpad",
            "vcredist", "updatehelper", "easyanticheat",
            "crashhandler", "crashsender", "crashuploader",
            "crashdump", "dedicatedserver", "serverbrowser",
            "configurationtool", "leveleditor", "unlocker", "patcher",
            "keygen", "saveeditor", "modmanager", "trainer"
        ];
        if (strongSubstrings.Any(fragment => normalizedStem.Contains(
                fragment,
                StringComparison.Ordinal)))
        {
            return true;
        }

        string[] hardSuffixes =
        [
            "installer", "uninstaller", "updater", "benchmark",
            "crashreporter", "crashhandler", "crashsender",
            "crashuploader", "dedicatedserver", "serverbrowser",
            "configtool", "configurationtool", "leveleditor",
            "unlocker", "patcher", "keygen", "saveeditor",
            "modmanager", "trainer"
        ];
        if (hardSuffixes.Any(suffix => normalizedStem.EndsWith(
                suffix,
                StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var fragment in IgnoredExecutableFragments)
        {
            var normalizedFragment = NormalizeName(fragment);
            if (normalizedStem.Equals(
                    normalizedFragment,
                    StringComparison.Ordinal)
                || LooksLikeInfrastructureExecutable(stem, fragment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBroadInstallRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            string[] broadUserFolders =
            [
                profile,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Path.Combine(profile, "Downloads")
            ];
            if (broadUserFolders
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => Path.GetFullPath(item).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))
                .Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            var name = Path.GetFileName(fullPath);
            if (GenericLibraryNames.Contains(name))
            {
                return true;
            }

            if (Directory.Exists(Path.Combine(fullPath, "Engine"))
                && (Directory.Exists(Path.Combine(fullPath, "Content"))
                    || Directory.Exists(Path.Combine(fullPath, "Binaries"))))
            {
                return false;
            }

            var gameLikeChildren = 0;
            foreach (var directory in Directory
                         .EnumerateDirectories(
                             fullPath,
                             "*",
                             SearchOption.TopDirectoryOnly)
                         .Take(40))
            {
                if (Directory.Exists(Path.Combine(directory, "Binaries"))
                    || Directory.EnumerateFiles(
                            directory,
                            "*.exe",
                            SearchOption.TopDirectoryOnly)
                        .Any(executable =>
                        {
                            var stem = Path.GetFileNameWithoutExtension(executable);
                            return !IsInfrastructureExecutableName(stem);
                        }))
                {
                    gameLikeChildren++;
                    if (gameLikeChildren >= 2)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            return true;
        }

        return false;
    }

    private IReadOnlyList<string> GetConfiguredLocalGameRoots()
    {
        if (_localGameRoots is not null)
        {
            return NormalizeRoots(_localGameRoots, requireExisting: false);
        }

        return LoadConfiguredLocalGameRoots();
    }

    private IReadOnlyList<string> GetAutomaticLocalGameRoots()
    {
        if (_automaticGameRoots is not null)
        {
            return NormalizeRoots(_automaticGameRoots, requireExisting: true);
        }

        // Переданные через конструктор корни используются тестами и
        // изолированными сценариями как полный override источников.
        if (_localGameRoots is not null)
        {
            return [];
        }

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory)
        };
        AddExistingPaths(
            roots,
            GetKnownLauncherLibraryRoots());
        var windowsRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    var driveRoot = drive.RootDirectory.FullName;
                    foreach (var name in GenericLibraryNames)
                    {
                        var candidate = Path.Combine(driveRoot, name);
                        if (Directory.Exists(candidate))
                        {
                            roots.Add(candidate);
                        }
                    }

                    var steamCommon = Path.Combine(
                        driveRoot,
                        "SteamLibrary",
                        "steamapps",
                        "common");
                    if (Directory.Exists(steamCommon))
                    {
                        roots.Add(steamCommon);
                    }

                    if (!string.Equals(
                            Path.GetFullPath(driveRoot),
                            Path.GetFullPath(windowsRoot ?? string.Empty),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // На несистемных дисках проверяем только прямых
                        // потомков корня и только при сильной игровой сигнатуре.
                        roots.Add(driveRoot);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }

        return NormalizeRoots(roots, requireExisting: true);
    }

    private static IEnumerable<string> GetKnownLauncherLibraryRoots()
    {
        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFiles, "EA Games"),
            Path.Combine(programFiles, "Riot Games"),
            Path.Combine(programFiles, "Rockstar Games"),
            Path.Combine(programFiles, "GOG Galaxy", "Games"),
            Path.Combine(programFilesX86, "Ubisoft", "Ubisoft Game Launcher", "games"),
            Path.Combine(programFilesX86, "Origin Games"),
            Path.Combine(programFilesX86, "GOG Galaxy", "Games"),
            Path.Combine(programFilesX86, "MY.GAMES"),
            Path.Combine(programFilesX86, "Mail.Ru"),
            Path.Combine(programFilesX86, "Gaijin"),
            Path.Combine(local, "MY.GAMES"),
            Path.Combine(local, "Mail.Ru", "GameCenter", "Games")
        ];
        return candidates.Where(Directory.Exists);
    }

    private static void AddExistingPaths(
        ICollection<string> target,
        IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && Directory.Exists(candidate))
            {
                target.Add(candidate);
            }
        }
    }

    private IReadOnlyList<string> LoadConfiguredLocalGameRoots()
    {
        return LibraryScanRoots.LoadConfiguredRoots(_localGameRootsPath);
    }

    private async Task SaveConfiguredLocalGameRootsAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_localGameRootsPath)
            ?? throw new InvalidOperationException(
                "Не удалось определить папку настроек Nexus.");
        Directory.CreateDirectory(directory);
        var snapshot = NormalizeRoots(roots, requireExisting: false);
        var temporaryPath = _localGameRootsPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(
                snapshot.OrderBy(
                    item => item,
                    StringComparer.OrdinalIgnoreCase),
                new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        File.Move(temporaryPath, _localGameRootsPath, overwrite: true);
    }

    private static IReadOnlyList<string> NormalizeRoots(
        IEnumerable<string> roots,
        bool requireExisting)
    {
        var normalized = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(root);
                if (!requireExisting || Directory.Exists(fullPath))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
            }
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool RequiresDirectGameSignature(string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var driveRoot = Path.GetPathRoot(fullPath)?
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            if (string.Equals(
                    fullPath,
                    driveRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            string[] broadUserFolders =
            [
                profile,
                Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyMusic),
                Path.Combine(profile, "Downloads")
            ];
            if (broadUserFolders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))
                .Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            var name = Path.GetFileName(fullPath);
            return name is not null
                && (name.Equals(
                        "Downloads",
                        StringComparison.OrdinalIgnoreCase)
                    || name.Equals(
                        "Загрузки",
                        StringComparison.OrdinalIgnoreCase)
                    || name.Equals(
                        "Desktop",
                        StringComparison.OrdinalIgnoreCase)
                    || name.Equals(
                        "Рабочий стол",
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return true;
        }
    }

    private static IEnumerable<GameEntry> MergeEquivalentGames(
        IEnumerable<GameEntry> games)
    {
        foreach (var pathGroup in games
                     .GroupBy(
                         game => Path.GetFullPath(game.InstallPath)
                             .TrimEnd(
                                 Path.DirectorySeparatorChar,
                                 Path.AltDirectorySeparatorChar),
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(
                         group => group.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            var clusters = new List<List<GameEntry>>();
            foreach (var game in pathGroup
                         .OrderByDescending(GetGameAuthority)
                         .ThenBy(
                             item => item.Name,
                             StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(
                             item => item.AppId,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(
                             item => item.LaunchTarget,
                             StringComparer.OrdinalIgnoreCase))
            {
                var matchingClusters = clusters
                    .Where(cluster => cluster.All(existing =>
                        AreEquivalentGames(existing, game)))
                    .ToArray();
                if (matchingClusters.Length == 1)
                {
                    matchingClusters[0].Add(game);
                }
                else
                {
                    clusters.Add([game]);
                }
            }

            foreach (var cluster in clusters)
            {
                yield return MergeDuplicateGames(cluster);
            }
        }
    }

    private static bool AreEquivalentGames(GameEntry left, GameEntry right)
    {
        var leftAppId = left.AppId?.Trim();
        var rightAppId = right.AppId?.Trim();
        var bothLocalExecutableIdentities =
            IsLocalExecutableIdentity(leftAppId)
            && IsLocalExecutableIdentity(rightAppId);
        if (bothLocalExecutableIdentities)
        {
            return string.Equals(
                leftAppId,
                rightAppId,
                StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(left.Source, right.Source, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(leftAppId)
            && !string.IsNullOrWhiteSpace(rightAppId))
        {
            return string.Equals(
                leftAppId,
                rightAppId,
                StringComparison.OrdinalIgnoreCase);
        }

        var leftLaunchPath = TryGetFullFilePath(left.LaunchTarget);
        var rightLaunchPath = TryGetFullFilePath(right.LaunchTarget);
        if (leftLaunchPath is not null && rightLaunchPath is not null
            && string.Equals(
                leftLaunchPath,
                rightLaunchPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftName = NormalizeName(left.Name);
        var rightName = NormalizeName(right.Name);
        if (leftName.Length == 0 || rightName.Length == 0)
        {
            return false;
        }

        if (leftName.Equals(rightName, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = Math.Min(leftName.Length, rightName.Length);
        var longer = Math.Max(leftName.Length, rightName.Length);
        return shorter >= 6
            && shorter / (double)longer >= 0.9
            && (leftName.Contains(rightName, StringComparison.Ordinal)
                || rightName.Contains(leftName, StringComparison.Ordinal));
    }

    private static string? TryGetFullFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !uri.IsFile)
        {
            return null;
        }

        try
        {
            return File.Exists(value) ? Path.GetFullPath(value) : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsLocalExecutableIdentity(string? value)
    {
        return value?.StartsWith(
            "local-exe:",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int GetGameAuthority(GameEntry game)
    {
        return game.Source switch
        {
            "Steam" => 500,
            "Epic Games" => 450,
            "Xbox" => 400,
            "Local" => 300,
            "Torrent" => 200,
            _ => 100
        };
    }

    private static GameEntry MergeDuplicateGames(
        IReadOnlyList<GameEntry> entries)
    {
        var ordered = entries
            .OrderByDescending(GetGameAuthority)
            .ThenBy(
                game => game.Name,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(
                game => game.AppId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var primary = ordered[0];
        var canDeleteFiles = entries.Any(game => game.CanDeleteFiles);
        var launchTarget = ordered
            .Select(game => game.LaunchTarget)
            .FirstOrDefault(target => !string.IsNullOrWhiteSpace(target));
        var appId = ordered
            .Select(game => game.AppId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var relatedTorrentPath = ordered
            .Select(game => game.RelatedTorrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var artworkPath = ordered
            .Select(game => game.ArtworkPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var detectionDetail = relatedTorrentPath is not null
            ? canDeleteFiles
                ? "Папка добавлена вами; связь с .torrent предположительная, состояние клиента не подтверждено"
                : "Связь с .torrent предположительная, состояние клиента не подтверждено"
            : primary.DetectionDetail;

        return primary with
        {
            LaunchTarget = launchTarget,
            AppId = appId,
            ArtworkPath = artworkPath,
            RelatedTorrentPath = relatedTorrentPath,
            DetectionDetail = detectionDetail,
            CanDeleteFiles = canDeleteFiles
        };
    }

    private static bool IsSafeTorrentContentName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 240
            || !string.Equals(name, name.Trim(), StringComparison.Ordinal)
            || name.EndsWith('.')
            || name is "." or ".."
            || Path.IsPathRooted(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        return true;
    }

    private static string? TryGetSafeChildDirectory(
        string root,
        string name)
    {
        if (!IsSafeTorrentContentName(name))
        {
            return null;
        }

        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, name));
            var prefix = fullRoot + Path.DirectorySeparatorChar;
            return candidate.StartsWith(
                       prefix,
                       StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(candidate)
                   && CanTraverseGameDirectory(candidate)
                ? candidate
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static HashSet<string> LoadPathSet(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))
                    ?.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(Path.GetFullPath)
                ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task SavePathSetAsync(
        string path,
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Не удалось определить папку настроек Nexus.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(
                values
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string? FindLocalArtwork(
        string installPath,
        IEnumerable<string?> identities,
        bool allowGenericNames)
    {
        var normalizedIdentities = identities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeName)
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] roots =
        [
            installPath,
            Path.Combine(installPath, "Artwork"),
            Path.Combine(installPath, "Art")
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var exact = Directory
                    .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedArtworkFile)
                    .Where(path => normalizedIdentities.Contains(
                        NormalizeName(Path.GetFileNameWithoutExtension(path)),
                        StringComparer.Ordinal))
                    .Select(path => (Path: path, Score: GetArtworkScore(path)))
                    .Where(candidate => candidate.Score >= 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(
                        candidate => candidate.Path,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(candidate => candidate.Path)
                    .FirstOrDefault();
                if (exact is not null)
                {
                    return exact;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
            }
        }

        if (!allowGenericNames)
        {
            return null;
        }

        string[] preferredNames =
        [
            "cover.jpg", "cover.png", "poster.jpg", "poster.png",
            "header.jpg", "header.png", "icon.png"
        ];
        return preferredNames
            .Select(name => Path.Combine(installPath, name))
            .Where(File.Exists)
            .FirstOrDefault(path => GetArtworkScore(path) >= 0);
    }

    private static string? TryGetExecutableIcon(string? executable)
    {
        return !string.IsNullOrWhiteSpace(executable)
               && File.Exists(executable)
            ? ShellIconCache.TryGetIconPath(executable)
            : null;
    }

    private static string? BuildLocalExecutableIdentity(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        try
        {
            return $"local-exe:{Path.GetFullPath(executable)}";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryGetProductName(string executable)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executable).ProductName?.Trim();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsGenericProductName(string name)
    {
        return name.Equals("Game", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Launcher", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Unity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    private IEnumerable<string> GetSteamRoots()
    {
        if (_steamRoots is not null)
        {
            return _steamRoots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var registryPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam",
                "SteamPath",
                null) as string;
            if (!string.IsNullOrWhiteSpace(registryPath))
            {
                roots.Add(registryPath.Replace('/', '\\'));
            }
        }

        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            roots.Add(Path.Combine(programFilesX86, "Steam"));
        }

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetSteamAppsDirectories(string steamRoot)
    {
        var directories = new List<string>
        {
            Path.Combine(steamRoot, "steamapps")
        };
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            return directories;
        }

        try
        {
            var contents = File.ReadAllText(libraryFile);
            foreach (Match match in ValvePathRegex().Matches(contents))
            {
                var libraryPath = match.Groups["path"].Value
                    .Replace(@"\\", @"\");
                if (!string.IsNullOrWhiteSpace(libraryPath))
                {
                    directories.Add(Path.Combine(libraryPath, "steamapps"));
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<string> GetXboxRoots()
    {
        if (_xboxRoots is not null)
        {
            return _xboxRoots;
        }

        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
    }

    private static Dictionary<string, string> ReadValveKeyValues(string path)
    {
        try
        {
            var contents = File.ReadAllText(path);
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ValveValueRegex().Matches(contents))
            {
                result[match.Groups["key"].Value] =
                    match.Groups["value"].Value;
            }

            return result;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindSteamArtwork(string steamRoot, string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var libraryCache = Path.Combine(steamRoot, "appcache", "librarycache");
        string[] candidates =
        [
            Path.Combine(libraryCache, appId, "library_600x900.jpg"),
            Path.Combine(libraryCache, appId, "library_600x900.png"),
            Path.Combine(libraryCache, $"{appId}_library_600x900.jpg"),
            Path.Combine(libraryCache, $"{appId}_library_600x900.png"),
            Path.Combine(libraryCache, appId, "library_capsule.jpg"),
            Path.Combine(libraryCache, appId, "library_capsule.png"),
            Path.Combine(libraryCache, appId, "library_hero.jpg"),
            Path.Combine(libraryCache, appId, "library_hero.png"),
            Path.Combine(libraryCache, appId, "header.jpg"),
            Path.Combine(libraryCache, appId, "header.png"),
            Path.Combine(libraryCache, $"{appId}_header.jpg"),
            Path.Combine(libraryCache, $"{appId}_header.png")
        ];
        var preferred = candidates.FirstOrDefault(File.Exists);
        if (preferred is not null)
        {
            return preferred;
        }

        var appCacheDirectory = Path.Combine(libraryCache, appId);
        var hashedCandidates = new List<string>();
        try
        {
            if (Directory.Exists(appCacheDirectory))
            {
                hashedCandidates.AddRange(Directory
                    .EnumerateFiles(
                        appCacheDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedArtworkFile)
                    .Take(120));
            }

            if (Directory.Exists(libraryCache))
            {
                hashedCandidates.AddRange(Directory
                    .EnumerateFiles(
                        libraryCache,
                        $"{appId}_*",
                        SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedArtworkFile)
                    .Take(120));
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
        }

        return hashedCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (
                Path: path,
                Score: GetArtworkScore(path)))
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static bool IsSupportedArtworkFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static long GetArtworkScore(string path)
    {
        try
        {
            using var image = Image.FromFile(path);
            if (image.Width < 256 || image.Height < 144)
            {
                return -1;
            }

            var area = (long)image.Width * image.Height;
            var aspectRatio = image.Width / (double)image.Height;
            var shapeBonus = aspectRatio is >= 0.55 and <= 0.85
                ? 4_000_000_000L
                : aspectRatio is >= 1.3 and <= 2.2
                    ? 2_000_000_000L
                    : 0;
            return shapeBonus + area;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or OutOfMemoryException
            or ExternalException
            or FileNotFoundException)
        {
            return -1;
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsSteamSystemComponent(string? appId, string name)
    {
        return string.Equals(appId, "228980", StringComparison.Ordinal)
               || name.Contains(
                   "Common Redistributables",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildEpicLaunchId(
        string? catalogNamespace,
        string? catalogItemId,
        string? appName)
    {
        if (!string.IsNullOrWhiteSpace(catalogNamespace)
            && !string.IsNullOrWhiteSpace(catalogItemId)
            && !string.IsNullOrWhiteSpace(appName))
        {
            return Uri.EscapeDataString(
                $"{catalogNamespace}:{catalogItemId}:{appName}");
        }

        var fallback = !string.IsNullOrWhiteSpace(appName)
            ? appName
            : catalogItemId;
        return string.IsNullOrWhiteSpace(fallback)
            ? null
            : Uri.EscapeDataString(fallback);
    }

    private sealed record TorrentDirectoryCandidate(
        string Path,
        int Score,
        int MatchQuality);

    private sealed record TorrentAssociation(string Path, string Detail);

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ValvePathRegex();

    [GeneratedRegex("\"(?<key>[^\"]+)\"\\s+\"(?<value>[^\"]*)\"")]
    private static partial Regex ValveValueRegex();

    [GeneratedRegex(
        @"(?:\s*[\[(].*?[\])])|(?:\s+(?:repack|репак|portable|rip|multi\d*|rus|eng|pc|x64|x86)(?:\s+.*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TorrentReleaseSuffixRegex();
}

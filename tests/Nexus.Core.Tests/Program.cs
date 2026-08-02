using Nexus.Core.Models;
using Nexus.Core.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace Nexus.Core.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tempBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NexusShell.Tests"));
        var testRoot = Path.GetFullPath(Path.Combine(tempBase, Guid.NewGuid().ToString("N")));

        if (!testRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Тестовый путь вышел за границы временного каталога.");
        }

        Directory.CreateDirectory(testRoot);

        try
        {
            Directory.CreateDirectory(Path.Combine(testRoot, "Документы"));
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "picture.png"), new byte[128]);
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "zeta.txt"), new byte[1536]);
            var hiddenFile = Path.Combine(testRoot, "desktop.ini");
            await File.WriteAllTextAsync(hiddenFile, "[.ShellClassInfo]");
            File.SetAttributes(
                hiddenFile,
                File.GetAttributes(hiddenFile) | FileAttributes.Hidden | FileAttributes.System);

            var service = new FileSystemService();
            var entries = await service.GetDirectoryEntriesAsync(testRoot);
            var navigation = service.GetNavigationTargets();
            Check(
                navigation.Single(item => item.Id == "games").Kind == NavigationKind.Games,
                "Раздел «Игры» должен быть отдельной библиотекой, а не папкой сохранений.");
            Check(
                navigation.Single(item => item.Id == "applications").Kind
                    == NavigationKind.Applications,
                "Раздел «Приложения» должен показывать ярлыки меню «Пуск».");
            Check(
                navigation.Single(item => item.Id == "torrents").Kind
                    == NavigationKind.Torrents,
                "Раздел «Торренты» должен быть отдельной коллекцией.");
            Check(
                navigation.Single(item => item.Id == "recent").Kind == NavigationKind.Recent,
                "«Последние» не должны подменяться корнем профиля.");
            Check(
                navigation.Single(item => item.Id == "favorites").Kind == NavigationKind.Favorites,
                "«Избранное» не должно подменяться папкой «Документы».");
            Check(
                navigation.Single(item => item.Id == "archive").Kind == NavigationKind.Archive,
                "Пустой архив не должен показывать содержимое «Загрузок».");
            Check(
                navigation.Single(item => item.Id == "archive").Path?.EndsWith(
                    "Архив Nexus",
                    StringComparison.Ordinal) == true,
                "Архив должен быть обычной физической папкой пользователя.");
            Check(
                navigation.Single(item => item.Id == "models").Kind == NavigationKind.OptionalFolder,
                "Неустановленная Ollama не должна приводить к открытию чужой папки.");
            Check(
                navigation.Single(item => item.Id == "mcp").Kind == NavigationKind.Collection,
                "Раздел MCP должен указывать на конфигурацию, а не на весь каталог Codex.");
            Check(
                navigation.Single(item => item.Id == "ai-projects").Kind
                    == NavigationKind.AiProjects
                && navigation.Single(item => item.Id == "ai-projects").Path is null,
                "AI-проекты должны быть виртуальным поиском по рабочим областям, а не одной папкой Documents\\Projects.");

            Check(entries.Count == 3, "Сервис должен вернуть все тестовые элементы.");
            Check(
                entries.All(item => item.Name != "desktop.ini"),
                "Служебные desktop.ini не должны засорять обычный список.");
            Check(entries[0].IsDirectory, "Папки должны отображаться перед файлами.");
            Check(entries[0].Name == "Документы", "Название папки должно сохраняться.");

            var image = entries.Single(item => item.Name == "picture.png");
            Check(image.DisplayType == "Изображение", "PNG должен определяться как изображение.");
            Check(
                Uri.TryCreate(image.ThumbnailSource, UriKind.Absolute, out var thumbnailUri)
                && thumbnailUri.IsFile,
                "WinUI должен получать локальные миниатюры как корректные file-URI.");

            var text = entries.Single(item => item.Name == "zeta.txt");
            Check(text.DisplayType == "Текстовый документ", "TXT должен определяться как текстовый документ.");
            Check(text.DisplaySize == "1,5 КБ" || text.DisplaySize == "1.5 КБ", "Размер файла должен форматироваться в КБ.");

            var torrentFile = Path.Combine(testRoot, "Nexus Quest.torrent");
            await File.WriteAllTextAsync(
                torrentFile,
                "d4:infod4:name11:Nexus Questee");
            var entriesWithTorrent = await service.GetDirectoryEntriesAsync(testRoot);
            Check(
                entriesWithTorrent.Single(item => item.Name == "Nexus Quest.torrent")
                    .DisplayType == "Торрент-файл",
                "Файлы .torrent должны определяться как торрент-файлы.");
            var torrentLimitRoot = Path.Combine(testRoot, "TorrentLimit");
            Directory.CreateDirectory(torrentLimitRoot);
            for (var index = 0; index < 505; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(
                        torrentLimitRoot,
                        $"limited-{index:D3}.torrent"),
                    "d4:infod4:name4:Gameee");
            }

            var limitedTorrents = await service.GetTorrentFilesAsync(
                [torrentLimitRoot]);
            Check(
                limitedTorrents.Count == 500,
                "Глобальный лимит .torrent должен соблюдаться даже внутри одной папки.");

            var metadataTorrentRoot = Path.Combine(
                testRoot,
                "TorrentMetadata");
            Directory.CreateDirectory(metadataTorrentRoot);
            var metadataTorrentPath = Path.Combine(
                metadataTorrentRoot,
                "metadata.torrent");
            const string tracker = "https://tracker.example/announce";
            var metadataTorrent =
                "d"
                + BencodeString("announce")
                + BencodeString(tracker)
                + BencodeString("announce-list")
                + "ll"
                + BencodeString(tracker)
                + "ee"
                + BencodeString("comment.utf-8")
                + BencodeString("Два файла")
                + BencodeString("created by")
                + BencodeString("Nexus Tests")
                + BencodeString("creation date")
                + "i1700000000e"
                + BencodeString("info")
                + "d"
                + BencodeString("files")
                + "l"
                + "d"
                + BencodeString("length")
                + "i123e"
                + BencodeString("path")
                + "l"
                + BencodeString("first.bin")
                + "e"
                + "e"
                + "d"
                + BencodeString("length")
                + "i456e"
                + BencodeString("path")
                + "l"
                + BencodeString("second.bin")
                + "e"
                + "e"
                + "e"
                + BencodeString("name.utf-8")
                + BencodeString("Metadata Game")
                + "e"
                + "e";
            await File.WriteAllBytesAsync(
                metadataTorrentPath,
                Encoding.UTF8.GetBytes(metadataTorrent));
            var metadataEntry = (await service.GetTorrentFilesAsync(
                    [metadataTorrentRoot]))
                .Single();
            Check(
                metadataEntry.TorrentMetadata is
                {
                    ContentName: "Metadata Game",
                    ContentFileCount: 2,
                    ContentSizeBytes: 579,
                    CreatedBy: "Nexus Tests",
                    Comment: "Два файла"
                }
                && metadataEntry.TorrentMetadata.Trackers.SequenceEqual(
                    [tracker],
                    StringComparer.OrdinalIgnoreCase),
                "Раздел торрентов должен получать имя, количество файлов, размер и трекеры из безопасно разобранного bencode.");

            var startMenuRoot = Path.Combine(testRoot, "StartMenu");
            var startMenuPrograms = Path.Combine(startMenuRoot, "Programs");
            Directory.CreateDirectory(startMenuPrograms);
            foreach (var appName in new[]
                     {
                         "Crossout",
                         "War Thunder",
                         "Warface",
                         "µTorrent"
                     })
            {
                await File.WriteAllTextAsync(
                    Path.Combine(startMenuRoot, $"{appName}.url"),
                    "[InternetShortcut]\nURL=https://example.invalid/");
            }

            await File.WriteAllTextAsync(
                Path.Combine(startMenuPrograms, "Nested App.url"),
                "[InternetShortcut]\nURL=https://example.invalid/nested");
            var applicationsService = new FileSystemService(
                Path.Combine(testRoot, "applications-view.json"),
                startMenuRoots: [startMenuRoot],
                startApplications:
                [
                    new StartApplicationInfo(
                        "Параметры",
                        @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                        IconSourcePath: image.FullPath)
                ]);
            var applications =
                await applicationsService.GetInstalledApplicationsAsync();
            Check(
                applications.Count == 6
                && new[]
                {
                    "Crossout",
                    "War Thunder",
                    "Warface",
                    "µTorrent",
                    "Nested App",
                    "Параметры"
                }.All(name => applications.Any(application =>
                    application.Name.Equals(
                        name,
                        StringComparison.CurrentCultureIgnoreCase))),
                "Приложения должны включать ярлыки прямо в корне Start Menu, вложенные Programs и Get-StartApps fallback.");
            var settingsApplication = applications.Single(
                application => application.Name == "Параметры");
            Check(
                settingsApplication.FullPath.StartsWith(
                    @"shell:AppsFolder\",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    settingsApplication.ThumbnailPath,
                    image.FullPath,
                    StringComparison.OrdinalIgnoreCase),
                "Get-StartApps запись должна сохранять запускаемый AppUserModelID и источник иконки.");

            var parsedStartApps = StartApplicationCatalog.ParseStartAppsJson(
                """
                [
                  { "Name": "Same Name", "AppID": "vendor.first!app" },
                  { "Name": "Same Name", "AppID": "vendor.second!app" },
                  { "Name": "Duplicate Alias", "AppID": "vendor.first!app" }
                ]
                """);
            Check(
                parsedStartApps.Count == 2
                && parsedStartApps.Select(application => application.ApplicationId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(
                        ["vendor.first!app", "vendor.second!app"]),
                "StartApplicationCatalog должен дедуплицировать по AppID, не склеивая разные приложения с одинаковым отображаемым именем.");
            var sameExecutable = Path.Combine(testRoot, "same-game.exe");
            var profileShortcutA = new StartApplicationInfo(
                "Game Profile A",
                Path.Combine(testRoot, "Profile A.lnk"),
                ResolvedExecutablePath: sameExecutable,
                SourcePath: Path.Combine(testRoot, "Profile A.lnk"));
            var profileShortcutB = new StartApplicationInfo(
                "Game Profile B",
                Path.Combine(testRoot, "Profile B.lnk"),
                ResolvedExecutablePath: sameExecutable,
                SourcePath: Path.Combine(testRoot, "Profile B.lnk"));
            Check(
                !string.Equals(
                    StartApplicationCatalog.GetStableIdentity(profileShortcutA),
                    StartApplicationCatalog.GetStableIdentity(profileShortcutB),
                    StringComparison.OrdinalIgnoreCase),
                "Разные ярлыки и профили одного EXE должны оставаться отдельными приложениями в каталоге Start.");

            var iconIdentityRoot = Path.Combine(testRoot, "IconIdentity");
            Directory.CreateDirectory(iconIdentityRoot);
            var iconExecutableA = Path.Combine(iconIdentityRoot, "GameA.exe");
            var iconExecutableB = Path.Combine(iconIdentityRoot, "GameB.exe");
            await File.WriteAllBytesAsync(iconExecutableA, new byte[128]);
            await File.WriteAllBytesAsync(iconExecutableB, new byte[128]);
            Check(
                !string.Equals(
                    ShellIconCache.GetCachePath(iconExecutableA),
                    ShellIconCache.GetCachePath(iconExecutableB),
                    StringComparison.OrdinalIgnoreCase),
                "Кэш иконок EXE должен быть привязан к конкретному пути, а не к общему расширению.");
            var mutableUrl = Path.Combine(iconIdentityRoot, "Mutable.url");
            var fixedTimestamp = new DateTime(
                2026,
                1,
                1,
                0,
                0,
                0,
                DateTimeKind.Utc);
            await File.WriteAllTextAsync(
                mutableUrl,
                "[InternetShortcut]\nURL=https://a.example/");
            File.SetLastWriteTimeUtc(mutableUrl, fixedTimestamp);
            var firstUrlIconIdentity = ShellIconCache.GetCachePath(mutableUrl);
            await File.WriteAllTextAsync(
                mutableUrl,
                "[InternetShortcut]\nURL=https://b.example/");
            File.SetLastWriteTimeUtc(mutableUrl, fixedTimestamp);
            var secondUrlIconIdentity = ShellIconCache.GetCachePath(mutableUrl);
            Check(
                !string.Equals(
                    firstUrlIconIdentity,
                    secondUrlIconIdentity,
                    StringComparison.OrdinalIgnoreCase),
                "Изменённый shortcut должен получать новый ключ иконки даже при одинаковом размере и timestamp.");

            var pathEntries = await service.GetPathEntriesAsync(
                [
                    Path.Combine(testRoot, "picture.png"),
                    Path.Combine(testRoot, "missing.txt")
                ]);
            Check(
                pathEntries.Count == 1 && pathEntries[0].Name == "picture.png",
                "Коллекции должны пропускать исчезнувшие элементы без ошибки.");

            var favoritesPath = Path.Combine(testRoot, "Settings", "favorites.json");
            var favorites = new FavoritesService(favoritesPath);
            Check(
                await favorites.AddAsync(Path.Combine(testRoot, "picture.png")),
                "Существующий файл должен добавляться в избранное.");
            Check(
                favorites.Contains(Path.Combine(testRoot, "picture.png")),
                "Добавленный файл должен находиться в избранном.");
            var reloadedFavorites = new FavoritesService(favoritesPath);
            Check(
                reloadedFavorites.Contains(Path.Combine(testRoot, "picture.png")),
                "Избранное должно сохраняться между запусками.");
            Check(
                await reloadedFavorites.RemoveAsync(Path.Combine(testRoot, "picture.png")),
                "Файл должен удаляться из избранного.");

            var operations = new FileOperationService();
            var operationRoot = Path.Combine(testRoot, "Operations");
            Directory.CreateDirectory(operationRoot);

            var created = await operations.CreateFolderAsync(operationRoot, "Новая папка");
            Check(created.Success, "Создание папки должно выполняться.");
            var createdPath = Path.Combine(operationRoot, "Новая папка");
            Check(Directory.Exists(createdPath), "Созданная папка должна существовать.");

            var duplicate = await operations.CreateFolderAsync(operationRoot, "Новая папка");
            Check(!duplicate.Success, "Сервис не должен молча объединять одноимённые папки.");

            var renamed = await operations.RenameAsync(createdPath, "Документы проекта");
            var renamedPath = Path.Combine(operationRoot, "Документы проекта");
            Check(renamed.Success && Directory.Exists(renamedPath), "Папка должна переименовываться.");

            var sourceFile = Path.Combine(operationRoot, "черновик.txt");
            await File.WriteAllTextAsync(sourceFile, "Nexus");
            var copyTarget = Path.Combine(operationRoot, "Копии");
            Directory.CreateDirectory(copyTarget);
            var copied = await operations.CopyAsync([sourceFile], copyTarget);
            Check(copied.Success, "Файл должен копироваться.");
            Check(File.Exists(Path.Combine(copyTarget, "черновик.txt")), "Копия файла должна существовать.");

            var moveTarget = Path.Combine(operationRoot, "Готово");
            Directory.CreateDirectory(moveTarget);
            var moved = await operations.MoveAsync([sourceFile], moveTarget);
            Check(moved.Success, "Файл должен перемещаться.");
            Check(!File.Exists(sourceFile), "После перемещения исходный файл должен исчезнуть.");
            Check(File.Exists(Path.Combine(moveTarget, "черновик.txt")), "Перемещённый файл должен существовать.");

            var conflictSource = Path.Combine(operationRoot, "конфликт.txt");
            await File.WriteAllTextAsync(conflictSource, "source");
            var conflictTarget = Path.Combine(operationRoot, "Конфликт назначения");
            Directory.CreateDirectory(conflictTarget);
            var conflictDestination = Path.Combine(conflictTarget, "конфликт.txt");
            await File.WriteAllTextAsync(conflictDestination, "destination");

            var conflictedMove = await operations.MoveAsync(
                [conflictSource],
                conflictTarget);
            Check(
                !conflictedMove.Success,
                "Перемещение не должно перезаписывать существующий файл.");
            Check(
                File.Exists(conflictSource)
                && await File.ReadAllTextAsync(conflictSource) == "source",
                "При конфликте исходный файл должен остаться неизменным.");
            Check(
                File.Exists(conflictDestination)
                && await File.ReadAllTextAsync(conflictDestination) == "destination",
                "При конфликте существующий файл назначения нельзя удалять или изменять.");

            var cancelledSource = Path.Combine(operationRoot, "Отменённая папка");
            Directory.CreateDirectory(cancelledSource);
            await File.WriteAllTextAsync(
                Path.Combine(cancelledSource, "данные.txt"),
                "safe");
            var cancelledTarget = Path.Combine(operationRoot, "Отменённое назначение");
            Directory.CreateDirectory(cancelledTarget);
            using (var cancelledMove = new CancellationTokenSource())
            {
                cancelledMove.Cancel();
                await CheckThrowsAsync<OperationCanceledException>(
                    () => operations.MoveAsync(
                        [cancelledSource],
                        cancelledTarget,
                        cancelledMove.Token),
                    "Отменённое перемещение должно завершаться отменой.");
            }

            Check(
                File.Exists(Path.Combine(cancelledSource, "данные.txt")),
                "При отмене исходная папка и её файлы должны сохраниться.");
            Check(
                !Directory.Exists(
                    Path.Combine(cancelledTarget, Path.GetFileName(cancelledSource))),
                "При отмене папка назначения не должна создаваться.");

            var unsafeName = await operations.CreateFolderAsync(operationRoot, "CON");
            Check(!unsafeName.Success, "Зарезервированные имена Windows должны отклоняться.");

            var gameTestRoot = Path.Combine(testRoot, "GameLibraries");
            var steamRoot = Path.Combine(gameTestRoot, "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            var portalPath = Path.Combine(steamApps, "common", "Portal 2");
            Directory.CreateDirectory(portalPath);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_620.acf"),
                """
                "AppState"
                {
                    "appid"       "620"
                    "name"        "Portal 2"
                    "installdir"  "Portal 2"
                }
                """);
            var portalArtworkDirectory = Path.Combine(
                steamRoot,
                "appcache",
                "librarycache",
                "620");
            Directory.CreateDirectory(portalArtworkDirectory);
            var portalHashedArtwork = Path.Combine(
                portalArtworkDirectory,
                "45f7b2c9a7a34e32a4f84c16aaf02c88.jpg");
            using (var bitmap = new Bitmap(600, 900))
            {
                bitmap.Save(portalHashedArtwork, ImageFormat.Jpeg);
            }
            var redistributablesPath = Path.Combine(
                steamApps,
                "common",
                "Steamworks Shared");
            Directory.CreateDirectory(redistributablesPath);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_228980.acf"),
                """
                "AppState"
                {
                    "appid"       "228980"
                    "name"        "Steamworks Common Redistributables"
                    "installdir"  "Steamworks Shared"
                }
                """);

            var epicManifests = Path.Combine(gameTestRoot, "EpicManifests");
            var epicGamePath = Path.Combine(gameTestRoot, "Epic", "Botany Manor");
            Directory.CreateDirectory(epicManifests);
            Directory.CreateDirectory(epicGamePath);
            await File.WriteAllTextAsync(
                Path.Combine(epicManifests, "botany.item"),
                $$"""
                {
                  "DisplayName": "Botany Manor",
                  "InstallLocation": "{{epicGamePath.Replace("\\", "\\\\")}}",
                  "AppName": "BotanyManor",
                  "CatalogItemId": "catalog-botany",
                  "CatalogNamespace": "namespace-botany"
                }
                """);

            var localGamesRoot = Path.Combine(gameTestRoot, "LocalLibrary");
            var localGamePath = Path.Combine(localGamesRoot, "Nexus Quest");
            Directory.CreateDirectory(localGamePath);
            await File.WriteAllBytesAsync(
                Path.Combine(localGamePath, "NexusQuest.exe"),
                new byte[96 * 1024]);

            var categoryPath = Path.Combine(localGamesRoot, "RPG");
            var categoryGameAPath = Path.Combine(categoryPath, "Astral One");
            var categoryGameBPath = Path.Combine(categoryPath, "Astral Two");
            Directory.CreateDirectory(categoryGameAPath);
            Directory.CreateDirectory(categoryGameBPath);
            await File.WriteAllBytesAsync(
                Path.Combine(categoryGameAPath, "AstralOne.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(categoryGameBPath, "AstralTwo.exe"),
                new byte[96 * 1024]);

            var torrentGamePath = Path.Combine(
                localGamesRoot,
                "Nexus Torrent Game");
            Directory.CreateDirectory(torrentGamePath);
            await File.WriteAllBytesAsync(
                Path.Combine(torrentGamePath, "NexusTorrentGame.exe"),
                new byte[96 * 1024]);
            var relatedTorrentPath = Path.Combine(
                localGamesRoot,
                "Nexus Torrent Game.torrent");
            await File.WriteAllTextAsync(
                relatedTorrentPath,
                "d4:infod4:name18:Nexus Torrent Gameee");

            var gamesService = new GameLibraryService(
                [steamRoot],
                epicManifests,
                [],
                [localGamesRoot],
                Path.Combine(gameTestRoot, "local-game-folders.json"),
                Path.Combine(gameTestRoot, "ignored-games.json"));
            Check(
                gamesService.GetLocalGameFolders().SequenceEqual(
                    [Path.GetFullPath(localGamesRoot)],
                    StringComparer.OrdinalIgnoreCase),
                "API настроек должен возвращать только явно настроенные игровые папки.");
            Check(
                gamesService.GetAutomaticGameFolders().Count == 0,
                "Явно переданные тестовые корни не должны смешиваться с автоматическими.");
            Check(
                gamesService.GetScanRoots().SequenceEqual(
                    [Path.GetFullPath(localGamesRoot)],
                    StringComparer.OrdinalIgnoreCase),
                "Test override не должен незаметно добавлять реальные Desktop, Downloads или диски.");
            var games = await gamesService.GetInstalledGamesAsync();
            Check(
                games.Count == 6,
                "Должны обнаруживаться Steam, Epic Games, локальные, вложенные и связанные с торрентом установки.");
            Check(
                games.Any(game =>
                    string.Equals(
                        game.InstallPath,
                        categoryGameAPath,
                        StringComparison.OrdinalIgnoreCase)
                    && game.CanDeleteFiles)
                && games.Any(game =>
                    string.Equals(
                        game.InstallPath,
                        categoryGameBPath,
                        StringComparison.OrdinalIgnoreCase)
                    && game.CanDeleteFiles)
                && games.All(game =>
                    !string.Equals(
                        game.InstallPath,
                        categoryPath,
                        StringComparison.OrdinalIgnoreCase)),
                "Категория с двумя играми должна давать две игры, а не ложную запись категории.");
            var steamGame = games.Single(game => game.Name == "Portal 2");
            Check(steamGame.Source == "Steam", "Источник Steam должен определяться.");
            Check(
                steamGame.LaunchTarget == "steam://rungameid/620",
                "Steam-игра должна запускаться по AppId.");
            Check(
                string.Equals(
                    steamGame.ArtworkPath,
                    portalHashedArtwork,
                    StringComparison.OrdinalIgnoreCase),
                "Steam должен использовать качественную hashed-обложку, а не растягивать маленькую иконку.");
            var epicGame = games.Single(game => game.Name == "Botany Manor");
            Check(epicGame.Source == "Epic Games", "Источник Epic Games должен определяться.");
            Check(
                epicGame.LaunchTarget?.StartsWith(
                    "com.epicgames.launcher://apps/namespace-botany%3Acatalog-botany%3ABotanyManor",
                    StringComparison.Ordinal) == true,
                "Epic-игра должна запускаться через Launcher.");
            var localGame = games.Single(game => game.Name == "Nexus Quest");
            Check(
                localGame.SourceLabel == "Локальная игра"
                && localGame.LaunchTarget?.EndsWith(
                    "NexusQuest.exe",
                    StringComparison.OrdinalIgnoreCase) == true,
                "Локальная игра должна запускаться напрямую через найденный EXE.");
            var gameArtwork = localGame with { ArtworkPath = image.FullPath };
            Check(
                Uri.TryCreate(gameArtwork.ArtworkSource, UriKind.Absolute, out var artworkUri)
                && artworkUri.IsFile,
                "Обложки игр должны передаваться WinUI как корректные file-URI.");
            var torrentGame = games.Single(game => game.Name == "Nexus Torrent Game");
            Check(
                torrentGame.Source == "Local"
                && string.Equals(
                    torrentGame.RelatedTorrentPath,
                    relatedTorrentPath,
                    StringComparison.OrdinalIgnoreCase),
                "Торрент должен оставаться доказательством связи, а не подменять подтверждённый локальный источник игры.");
            Check(
                torrentGame.CanDeleteFiles
                && torrentGame.DetectionDetail?.Contains(
                    "предположительная",
                    StringComparison.CurrentCultureIgnoreCase) == true,
                "При объединении torrent/local доверие пользовательской папки должно сохраняться, а связь с клиентом не должна выдаваться за подтверждённую.");

            var automaticDiscoveryRoot = Path.Combine(
                gameTestRoot,
                "SyntheticProfile",
                "Downloads");
            var portableGamePath = Path.Combine(
                automaticDiscoveryRoot,
                "Independent Portable");
            Directory.CreateDirectory(portableGamePath);
            await File.WriteAllBytesAsync(
                Path.Combine(portableGamePath, "IndependentPortable.exe"),
                new byte[96 * 1024]);
            var automaticGamesService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "automatic-local-game-folders.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "automatic-ignored-games.json"),
                automaticGameRoots: [automaticDiscoveryRoot]);
            var automaticallyFoundGames =
                await automaticGamesService.GetInstalledGamesAsync();
            var portableGame = automaticallyFoundGames.Single(game =>
                string.Equals(
                    Path.GetFullPath(game.InstallPath),
                    Path.GetFullPath(portableGamePath),
                    StringComparison.OrdinalIgnoreCase));
            Check(
                portableGame.Source == "Local"
                && portableGame.CanLaunch
                && !portableGame.CanDeleteFiles,
                "Portable-игра должна определяться без лаунчера, но авто-корень не должен давать право удаления.");
            Check(
                automaticGamesService.GetLocalGameFolders().Count == 0
                && automaticGamesService.GetAutomaticGameFolders()
                    .Contains(
                        Path.GetFullPath(automaticDiscoveryRoot),
                        StringComparer.OrdinalIgnoreCase),
                "Configured и automatic game roots должны быть разделены в публичном API.");

            var shortcutGamePath = Path.Combine(
                gameTestRoot,
                "Launcher Source Game");
            Directory.CreateDirectory(shortcutGamePath);
            var shortcutGameExecutable = Path.Combine(
                shortcutGamePath,
                "LauncherSourceGame.exe");
            await File.WriteAllBytesAsync(
                shortcutGameExecutable,
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(shortcutGamePath, "UnityPlayer.dll"),
                [1]);
            var shortcutPath = Path.Combine(
                gameTestRoot,
                "Launcher Source Game.lnk");
            await File.WriteAllBytesAsync(shortcutPath, [1]);

            var ordinaryAppPath = Path.Combine(gameTestRoot, "Editor Tool");
            Directory.CreateDirectory(ordinaryAppPath);
            var ordinaryAppExecutable = Path.Combine(
                ordinaryAppPath,
                "EditorTool.exe");
            await File.WriteAllBytesAsync(
                ordinaryAppExecutable,
                new byte[96 * 1024]);
            var ordinaryAppShortcut = Path.Combine(
                gameTestRoot,
                "Editor Tool.lnk");
            await File.WriteAllBytesAsync(ordinaryAppShortcut, [1]);
            var advertisedGameShortcut = Path.Combine(
                gameTestRoot,
                "Crossout.lnk");
            await File.WriteAllBytesAsync(advertisedGameShortcut, [1]);
            var launcherOnlyShortcut = Path.Combine(
                gameTestRoot,
                "Epic Games Launcher.lnk");
            await File.WriteAllBytesAsync(launcherOnlyShortcut, [1]);
            var shortcutGamesService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingShortcutEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "shortcut-local-game-folders.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "shortcut-ignored-games.json"),
                automaticGameRoots: [],
                startApplications:
                [
                    new StartApplicationInfo(
                        "Launcher Source Game",
                        shortcutPath,
                        ResolvedExecutablePath: shortcutGameExecutable,
                        WorkingDirectory: shortcutGamePath,
                        SourcePath: shortcutPath),
                    new StartApplicationInfo(
                        "Editor Tool",
                        ordinaryAppShortcut,
                        ResolvedExecutablePath: ordinaryAppExecutable,
                        WorkingDirectory: ordinaryAppPath,
                        SourcePath: ordinaryAppShortcut),
                    new StartApplicationInfo(
                        "Crossout",
                        advertisedGameShortcut,
                        ResolvedExecutablePath: Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.Windows),
                            "explorer.exe"),
                        Arguments: "url=\"https://example.invalid/game\"",
                        SourcePath: advertisedGameShortcut),
                    new StartApplicationInfo(
                        "Epic Games Launcher",
                        launcherOnlyShortcut,
                        ResolvedExecutablePath: shortcutGameExecutable,
                        WorkingDirectory: shortcutGamePath,
                        SourcePath: launcherOnlyShortcut)
                ]);
            var shortcutGames =
                await shortcutGamesService.GetInstalledGamesAsync();
            Check(
                shortcutGames.Count == 1
                && string.Equals(
                    shortcutGames[0].InstallPath,
                    shortcutGamePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    shortcutGames[0].LaunchTarget,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase)
                && !shortcutGames[0].CanDeleteFiles,
                "Безопасный ярлык стороннего лаунчера должен находить игру, но обычные программы и рекламные web-ярлыки не должны становиться играми.");

            var nestedTorrentRoot = Path.Combine(
                gameTestRoot,
                "NestedTorrent",
                "Downloads");
            var nestedTorrentGamePath = Path.Combine(
                nestedTorrentRoot,
                "Completed",
                "Need for Speed Underground");
            Directory.CreateDirectory(nestedTorrentGamePath);
            await File.WriteAllBytesAsync(
                Path.Combine(
                    nestedTorrentGamePath,
                    "NeedForSpeedUnderground.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(nestedTorrentGamePath, "UnityPlayer.dll"),
                [1]);
            var nestedTorrentPath = Path.Combine(
                nestedTorrentRoot,
                "Need for Speed Underground.torrent");
            const string nestedTorrentName =
                "Need for Speed Underground (2003) PC [РУС] Repack by Nexus";
            await File.WriteAllTextAsync(
                nestedTorrentPath,
                $"d4:infod4:name{Encoding.UTF8.GetByteCount(nestedTorrentName)}:{nestedTorrentName}ee");
            var nestedTorrentService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingNestedTorrentEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "nested-torrent-local-folders.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "nested-torrent-ignored-games.json"),
                automaticGameRoots: [nestedTorrentRoot],
                startApplications: []);
            var nestedTorrentGames =
                await nestedTorrentService.GetInstalledGamesAsync();
            Check(
                nestedTorrentGames.Any(game =>
                    string.Equals(
                        game.InstallPath,
                        nestedTorrentGamePath,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        game.RelatedTorrentPath,
                        nestedTorrentPath,
                        StringComparison.OrdinalIgnoreCase)),
                "Торрент с release-суффиксом должен безопасно связываться с игрой во вложенной папке загрузки.");

            var safeTorrentRoot = Path.Combine(
                gameTestRoot,
                "TorrentSafety",
                "Inbox");
            var escapedGamePath = Path.Combine(
                gameTestRoot,
                "TorrentSafety",
                "Escaped Game");
            Directory.CreateDirectory(safeTorrentRoot);
            Directory.CreateDirectory(escapedGamePath);
            await File.WriteAllBytesAsync(
                Path.Combine(escapedGamePath, "EscapedGame.exe"),
                new byte[96 * 1024]);
            var maliciousContentName =
                $"..{Path.DirectorySeparatorChar}Escaped Game";
            await File.WriteAllTextAsync(
                Path.Combine(safeTorrentRoot, "metadata-escape.torrent"),
                $"d4:infod4:name{Encoding.UTF8.GetByteCount(maliciousContentName)}:{maliciousContentName}ee");
            var shortMatchGamePath = Path.Combine(
                safeTorrentRoot,
                "Alpha Game");
            var ambiguousGameAPath = Path.Combine(
                safeTorrentRoot,
                "Shared Game Deluxe");
            var ambiguousGameBPath = Path.Combine(
                safeTorrentRoot,
                "Shared Game Gold");
            Directory.CreateDirectory(shortMatchGamePath);
            Directory.CreateDirectory(ambiguousGameAPath);
            Directory.CreateDirectory(ambiguousGameBPath);
            await File.WriteAllBytesAsync(
                Path.Combine(shortMatchGamePath, "AlphaGame.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(ambiguousGameAPath, "SharedGameDeluxe.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(ambiguousGameBPath, "SharedGameGold.exe"),
                new byte[96 * 1024]);
            const string shortTorrentName = "A";
            var shortTorrentPath = Path.Combine(
                safeTorrentRoot,
                "short.torrent");
            await File.WriteAllTextAsync(
                shortTorrentPath,
                $"d4:infod4:name{Encoding.UTF8.GetByteCount(shortTorrentName)}:{shortTorrentName}ee");
            const string ambiguousTorrentName = "Shared Game";
            var ambiguousTorrentPath = Path.Combine(
                safeTorrentRoot,
                "shared-match.torrent");
            await File.WriteAllTextAsync(
                ambiguousTorrentPath,
                $"d4:infod4:name{Encoding.UTF8.GetByteCount(ambiguousTorrentName)}:{ambiguousTorrentName}ee");
            var torrentSafetyService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingSafetyEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "safety-local-game-folders.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "safety-ignored-games.json"),
                automaticGameRoots: [safeTorrentRoot]);
            var safetyGames = await torrentSafetyService.GetInstalledGamesAsync();
            Check(
                safetyGames.All(game =>
                    !string.Equals(
                        Path.GetFullPath(game.InstallPath),
                        Path.GetFullPath(escapedGamePath),
                        StringComparison.OrdinalIgnoreCase)),
                "Имя из .torrent не должно выходить через .. за пределы проверяемого корня.");
            Check(
                safetyGames.All(game =>
                    !string.Equals(
                        game.RelatedTorrentPath,
                        shortTorrentPath,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        game.RelatedTorrentPath,
                        ambiguousTorrentPath,
                        StringComparison.OrdinalIgnoreCase)),
                "Короткое или неоднозначное fuzzy-имя торрента не должно связываться автоматически.");

            var sharedSteamRoot = Path.Combine(
                gameTestRoot,
                "SharedIdentitySteam");
            var sharedSteamApps = Path.Combine(sharedSteamRoot, "steamapps");
            var sharedInstallPath = Path.Combine(
                sharedSteamApps,
                "common",
                "Shared Runtime");
            Directory.CreateDirectory(sharedInstallPath);
            await File.WriteAllTextAsync(
                Path.Combine(sharedSteamApps, "appmanifest_7001.acf"),
                """
                "AppState"
                {
                    "appid"       "7001"
                    "name"        "North Star"
                    "installdir"  "Shared Runtime"
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sharedSteamApps, "appmanifest_7002.acf"),
                """
                "AppState"
                {
                    "appid"       "7002"
                    "name"        "South Star"
                    "installdir"  "Shared Runtime"
                }
                """);
            var northArtworkDirectory = Path.Combine(
                sharedSteamRoot,
                "appcache",
                "librarycache",
                "7001");
            var southArtworkDirectory = Path.Combine(
                sharedSteamRoot,
                "appcache",
                "librarycache",
                "7002");
            Directory.CreateDirectory(northArtworkDirectory);
            Directory.CreateDirectory(southArtworkDirectory);
            var northArtwork = Path.Combine(
                northArtworkDirectory,
                "library_600x900.jpg");
            var southArtwork = Path.Combine(
                southArtworkDirectory,
                "library_600x900.jpg");
            using (var bitmap = new Bitmap(600, 900))
            {
                bitmap.Save(northArtwork, ImageFormat.Jpeg);
                bitmap.Save(southArtwork, ImageFormat.Jpeg);
            }

            var sharedIdentityService = new GameLibraryService(
                steamRoots: [sharedSteamRoot],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingSharedIdentityEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "shared-identity-local-roots.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "shared-identity-ignored.json"),
                automaticGameRoots: [],
                startApplications: []);
            var sharedInstallGames = await sharedIdentityService
                .GetInstalledGamesAsync();
            Check(
                sharedInstallGames.Count == 2
                && sharedInstallGames.Select(game => game.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(["North Star", "South Star"])
                && string.Equals(
                    sharedInstallGames.Single(game => game.AppId == "7001")
                        .ArtworkPath,
                    northArtwork,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    sharedInstallGames.Single(game => game.AppId == "7002")
                        .ArtworkPath,
                    southArtwork,
                    StringComparison.OrdinalIgnoreCase),
                "Одинаковый install path не должен склеивать разные manifest ID или менять их обложки местами.");

            var repackRoot = Path.Combine(
                gameTestRoot,
                "PortableProfile",
                "Downloads");
            var repackGamePath = Path.Combine(
                repackRoot,
                "Peach Repack [Nexus]");
            Directory.CreateDirectory(repackGamePath);
            var peachExecutable = Path.Combine(repackGamePath, "Peach.exe");
            await File.WriteAllBytesAsync(
                peachExecutable,
                new byte[128 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(repackGamePath, "GameLauncher.exe"),
                new byte[2 * 1024 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(repackGamePath, "unins000.exe"),
                new byte[3 * 1024 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(repackGamePath, "UnityCrashHandler64.exe"),
                new byte[4 * 1024 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(repackGamePath, "UnityPlayer.dll"),
                [1]);
            var peachArtwork = Path.Combine(repackGamePath, "Peach.png");
            var unrelatedGenericArtwork = Path.Combine(
                repackGamePath,
                "cover.jpg");
            using (var bitmap = new Bitmap(512, 512))
            {
                bitmap.Save(peachArtwork, ImageFormat.Png);
                bitmap.Save(unrelatedGenericArtwork, ImageFormat.Jpeg);
            }

            var repackService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingRepackEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "repack-local-roots.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "repack-ignored.json"),
                automaticGameRoots: [repackRoot],
                startApplications: []);
            var repackGames = await repackService.GetInstalledGamesAsync();
            Check(
                repackGames.Count == 1
                && string.Equals(
                    repackGames[0].LaunchTarget,
                    peachExecutable,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    repackGames[0].ArtworkPath,
                    peachArtwork,
                    StringComparison.OrdinalIgnoreCase)
                && !repackGames[0].CanDeleteFiles,
                "Portable/repack должен находиться по игровым файлам, выбирать конкретный game EXE и не выбирать launcher, uninstaller или crash reporter.");

            var peachShortcutA = Path.Combine(
                gameTestRoot,
                "Peach DirectX 11.lnk");
            var peachShortcutB = Path.Combine(
                gameTestRoot,
                "Peach DirectX 12.lnk");
            await File.WriteAllBytesAsync(peachShortcutA, [1]);
            await File.WriteAllBytesAsync(peachShortcutB, [2]);
            var shortcutIdentityService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingShortcutIdentityEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "shortcut-identity-local-roots.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "shortcut-identity-ignored.json"),
                automaticGameRoots: [],
                startApplications:
                [
                    new StartApplicationInfo(
                        "Peach DirectX 11",
                        peachShortcutA,
                        ResolvedExecutablePath: peachExecutable,
                        WorkingDirectory: repackGamePath,
                        SourcePath: peachShortcutA),
                    new StartApplicationInfo(
                        "Peach DirectX 12",
                        peachShortcutB,
                        ResolvedExecutablePath: peachExecutable,
                        WorkingDirectory: repackGamePath,
                        SourcePath: peachShortcutB)
                ]);
            var shortcutIdentityGames = await shortcutIdentityService
                .GetInstalledGamesAsync();
            Check(
                shortcutIdentityGames.Count == 1
                && shortcutIdentityGames[0].AppId?.StartsWith(
                    "local-exe:",
                    StringComparison.OrdinalIgnoreCase) == true,
                "Несколько ярлыков одного EXE должны давать одну игру со стабильной executable identity.");

            var honestTorrentRoot = Path.Combine(
                gameTestRoot,
                "HonestTorrentAssociation");
            var actualPayloadPath = Path.Combine(
                honestTorrentRoot,
                "Actual Payload");
            var misleadingPayloadPath = Path.Combine(
                honestTorrentRoot,
                "Misleading Name");
            Directory.CreateDirectory(actualPayloadPath);
            Directory.CreateDirectory(misleadingPayloadPath);
            await File.WriteAllBytesAsync(
                Path.Combine(actualPayloadPath, "ActualPayload.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(actualPayloadPath, "UnityPlayer.dll"),
                [1]);
            await File.WriteAllBytesAsync(
                Path.Combine(misleadingPayloadPath, "MisleadingName.exe"),
                new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(misleadingPayloadPath, "UnityPlayer.dll"),
                [1]);
            const string actualPayloadName = "Actual Payload";
            var renamedTorrentPath = Path.Combine(
                honestTorrentRoot,
                "Misleading Name.torrent");
            await File.WriteAllTextAsync(
                renamedTorrentPath,
                $"d4:infod4:name{Encoding.UTF8.GetByteCount(actualPayloadName)}:{actualPayloadName}ee");
            var honestTorrentService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingHonestTorrentEpicManifests"),
                xboxRoots: [],
                localGameRoots: [],
                localGameRootsPath: Path.Combine(
                    gameTestRoot,
                    "honest-torrent-local-roots.json"),
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "honest-torrent-ignored.json"),
                automaticGameRoots: [honestTorrentRoot],
                startApplications: []);
            var honestTorrentGames = await honestTorrentService
                .GetInstalledGamesAsync();
            Check(
                string.Equals(
                    honestTorrentGames.Single(game => string.Equals(
                        game.InstallPath,
                        actualPayloadPath,
                        StringComparison.OrdinalIgnoreCase)).RelatedTorrentPath,
                    renamedTorrentPath,
                    StringComparison.OrdinalIgnoreCase)
                && honestTorrentGames.Single(game => string.Equals(
                    game.InstallPath,
                    misleadingPayloadPath,
                    StringComparison.OrdinalIgnoreCase)).RelatedTorrentPath is null,
                "Внутреннее content name должно иметь приоритет над переименованным .torrent-файлом.");

            var mutableRootsPath = Path.Combine(
                gameTestRoot,
                "mutable-local-game-folders.json");
            var mutableRootA = Path.Combine(gameTestRoot, "Mutable A");
            var mutableRootB = Path.Combine(gameTestRoot, "Mutable B");
            Directory.CreateDirectory(mutableRootA);
            Directory.CreateDirectory(mutableRootB);
            var mutableRootsService = new GameLibraryService(
                steamRoots: [],
                epicManifestDirectory: Path.Combine(
                    gameTestRoot,
                    "MissingMutableEpicManifests"),
                xboxRoots: [],
                localGameRoots: null,
                localGameRootsPath: mutableRootsPath,
                ignoredGamesPath: Path.Combine(
                    gameTestRoot,
                    "mutable-ignored-games.json"),
                automaticGameRoots: []);
            var addedRoots = await Task.WhenAll(
                mutableRootsService.AddLocalGameFolderAsync(mutableRootA),
                mutableRootsService.AddLocalGameFolderAsync(mutableRootB));
            Check(
                addedRoots.All(result => result)
                && mutableRootsService.GetLocalGameFolders().Count == 2,
                "Параллельное добавление источников не должно терять одну из записей.");
            var removedRoots = await Task.WhenAll(
                mutableRootsService.RemoveLocalGameFolderAsync(mutableRootA),
                mutableRootsService.RemoveLocalGameFolderAsync(mutableRootB));
            Check(
                removedRoots.All(result => result)
                && mutableRootsService.GetLocalGameFolders().Count == 0,
                "Параллельное удаление источников должно быть сериализовано.");
            Check(
                await mutableRootsService.AddLocalGameFolderAsync(mutableRootA),
                "Существующий источник должен повторно добавляться.");
            Directory.Delete(mutableRootA);
            Check(
                mutableRootsService.GetLocalGameFolders().Contains(
                    Path.GetFullPath(mutableRootA),
                    StringComparer.OrdinalIgnoreCase)
                && await mutableRootsService.RemoveLocalGameFolderAsync(
                    mutableRootA),
                "Недоступный configured root должен оставаться видимым и удаляемым в настройках.");
            var categoryGames = games
                .Where(game =>
                    string.Equals(
                        game.InstallPath,
                        categoryGameAPath,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        game.InstallPath,
                        categoryGameBPath,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var hiddenCategoryResults = await Task.WhenAll(
                categoryGames.Select(game =>
                    gamesService.HideGameAsync(game)));
            Check(
                hiddenCategoryResults.All(result => result)
                && gamesService.HiddenGamesCount == 2
                && await gamesService.RestoreHiddenGamesAsync() == 2,
                "Параллельное скрытие игр не должно конфликтовать в ignored-games.json.");
            Check(
                await gamesService.HideGameAsync(torrentGame),
                "Игру должно быть можно скрыть из библиотеки без удаления файлов.");
            Check(
                gamesService.HiddenGamesCount == 1
                && (await gamesService.GetInstalledGamesAsync()).Count == 5,
                "Скрытая игра не должна отображаться в библиотеке.");
            Check(
                await gamesService.RestoreHiddenGamesAsync() == 1
                && (await gamesService.GetInstalledGamesAsync()).Count == 6,
                "Скрытые игры должны восстанавливаться одной командой.");

            var aiSearchRoot = Path.Combine(testRoot, "AiDiscovery");
            var dependencyProject = Path.Combine(
                aiSearchRoot,
                "Assistant Workspace");
            var modelProject = Path.Combine(
                aiSearchRoot,
                "Local Model Lab");
            var ordinaryProject = Path.Combine(
                aiSearchRoot,
                "Budget Tracker");
            var codexTask = Path.Combine(
                aiSearchRoot,
                "Codex",
                "2026-07-29",
                "nexus-shell-task");
            var serviceEnvironment = Path.Combine(
                aiSearchRoot,
                ".codex");
            var agentWorkspace = Path.Combine(
                aiSearchRoot,
                "Research Agents");
            var sourceOnlyProject = Path.Combine(
                aiSearchRoot,
                "Vision Helper");
            Directory.CreateDirectory(dependencyProject);
            Directory.CreateDirectory(modelProject);
            Directory.CreateDirectory(ordinaryProject);
            Directory.CreateDirectory(Path.Combine(codexTask, "work"));
            Directory.CreateDirectory(Path.Combine(codexTask, "outputs"));
            Directory.CreateDirectory(Path.Combine(codexTask, ".codex"));
            Directory.CreateDirectory(Path.Combine(aiSearchRoot, "Codex", ".git"));
            Directory.CreateDirectory(Path.Combine(aiSearchRoot, "Codex", ".codex"));
            Directory.CreateDirectory(Path.Combine(serviceEnvironment, "skills"));
            Directory.CreateDirectory(Path.Combine(agentWorkspace, "agents"));
            Directory.CreateDirectory(sourceOnlyProject);
            await File.WriteAllTextAsync(
                Path.Combine(dependencyProject, "package.json"),
                """
                {
                  "dependencies": {
                    "openai": "^5.0.0"
                  }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(modelProject, "README.md"),
                "# Local model");
            await File.WriteAllBytesAsync(
                Path.Combine(modelProject, "nexus.gguf"),
                new byte[128 * 1024]);
            await File.WriteAllTextAsync(
                Path.Combine(ordinaryProject, "package.json"),
                """
                {
                  "dependencies": {
                    "sqlite": "^5.0.0"
                  }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(ordinaryProject, "README.md"),
                "# Budget tracker");
            await File.WriteAllTextAsync(
                Path.Combine(agentWorkspace, "package.json"),
                """
                {
                  "dependencies": {
                    "@anthropic-ai/sdk": "^1.0.0"
                  }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sourceOnlyProject, "assistant.py"),
                """
                from openai import OpenAI
                client = OpenAI()
                """);

            var aiWorkspace = new AiWorkspaceService(
                searchRoots: [aiSearchRoot, aiSearchRoot],
                includeDefaultRoots: false,
                maximumDepth: 3,
                maximumVisitedDirectories: 500,
                maximumResults: 50);
            var aiProjects = await aiWorkspace.DiscoverProjectsAsync();
            Check(
                aiWorkspace.GetSearchRoots().Count == 1
                && aiProjects.Count(project =>
                    string.Equals(
                        project.FullPath,
                        dependencyProject,
                        StringComparison.OrdinalIgnoreCase)) == 1,
                "AI-поиск должен дедуплицировать корни и находить проект по зависимости OpenAI.");
            Check(
                aiProjects.Single(project =>
                    string.Equals(
                        project.FullPath,
                        modelProject,
                        StringComparison.OrdinalIgnoreCase)).Kind
                    == AiProjectKind.ModelWorkspace,
                "Файлы моделей должны определять рабочую область моделей.");
            Check(
                aiProjects.All(project =>
                    !string.Equals(
                        project.FullPath,
                        ordinaryProject,
                        StringComparison.OrdinalIgnoreCase)),
                "Обычный проект без AI-сигналов не должен попадать в AI-проекты.");
            Check(
                aiProjects.Any(project =>
                    string.Equals(
                        project.FullPath,
                        agentWorkspace,
                        StringComparison.OrdinalIgnoreCase)
                    && project.Kind == AiProjectKind.AgentWorkspace),
                "Полноценные agent-workspace проекты нельзя скрывать из AI-проектов.");
            Check(
                aiProjects.Any(project =>
                    string.Equals(
                        project.FullPath,
                        sourceOnlyProject,
                        StringComparison.OrdinalIgnoreCase)),
                "AI-проект без манифеста должен находиться по реальному использованию SDK в коде.");
            var customAiRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "CustomAiRoot")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(customAiRoot, "agent.py"),
                "import ollama");
            var customRootsSettings = Path.Combine(
                testRoot,
                "ai-search-roots.txt");
            var configurableAiWorkspace = new AiWorkspaceService(
                includeDefaultRoots: false,
                customRootsFilePath: customRootsSettings);
            Check(
                configurableAiWorkspace.AddSearchRoot(customAiRoot)
                && !configurableAiWorkspace.AddSearchRoot(customAiRoot)
                && configurableAiWorkspace.GetCustomSearchRoots().Contains(
                    customAiRoot,
                    StringComparer.OrdinalIgnoreCase),
                "Пользовательский источник AI-проектов должен добавляться один раз.");
            var reloadedAiWorkspace = new AiWorkspaceService(
                includeDefaultRoots: false,
                customRootsFilePath: customRootsSettings);
            Check(
                reloadedAiWorkspace.GetSearchRoots().Contains(
                    customAiRoot,
                    StringComparer.OrdinalIgnoreCase)
                && (await reloadedAiWorkspace.DiscoverProjectsAsync())
                .Any(project => string.Equals(
                    project.FullPath,
                    customAiRoot,
                    StringComparison.OrdinalIgnoreCase)),
                "Добавленный AI-источник должен сохраняться и участвовать в следующем поиске.");
            Check(
                reloadedAiWorkspace.RemoveSearchRoot(customAiRoot)
                && reloadedAiWorkspace.GetCustomSearchRoots().Count == 0,
                "Пользовательский AI-источник должен удаляться из настроек.");
            Check(
                aiProjects.Any(project =>
                    string.Equals(
                        project.FullPath,
                        codexTask,
                        StringComparison.OrdinalIgnoreCase)
                    && project.Kind == AiProjectKind.Project)
                && aiProjects.All(project =>
                    !string.Equals(
                        project.FullPath,
                        Path.Combine(codexTask, ".codex"),
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        project.FullPath,
                        serviceEnvironment,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        project.FullPath,
                        Path.Combine(aiSearchRoot, "Codex"),
                        StringComparison.OrdinalIgnoreCase)),
                "AI-поиск должен показывать задачи Codex, а не служебные .codex или общий контейнер.");
            using (var cancelledAiDiscovery =
                   new CancellationTokenSource())
            {
                cancelledAiDiscovery.Cancel();
                await CheckThrowsAsync<OperationCanceledException>(
                    () => aiWorkspace.DiscoverProjectsAsync(
                        cancelledAiDiscovery.Token),
                    "Отменённый поиск AI-проектов должен завершаться отменой.");
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await CheckThrowsAsync<OperationCanceledException>(
                () => service.GetDirectoryEntriesAsync(testRoot, cancellation.Token),
                "Отменённое чтение должно завершаться отменой.");

            var profile = Path.Combine(testRoot, "Profile");
            var downloads = Path.Combine(profile, "Downloads");
            Directory.CreateDirectory(downloads);
            var installerPath = Path.Combine(downloads, "setup.msi");
            await File.WriteAllBytesAsync(installerPath, new byte[64]);

            var organization = new OrganizationService(profile);
            var suggestion = organization.Suggest(installerPath);
            Check(suggestion is not null, "Для установщика во «Входящих» должно появиться предложение.");
            Check(
                suggestion!.DestinationLabel == "Загрузки › Установщики",
                "Установщик должен предлагаться в соответствующую папку.");

            var applied = await organization.ApplyAsync(suggestion);
            Check(applied.Success && applied.Operation is not null, "Подтверждённое перемещение должно выполниться.");
            Check(File.Exists(applied.Operation!.DestinationPath), "Файл должен появиться в папке назначения.");

            var undone = await organization.UndoAsync(applied.Operation);
            Check(undone.Success, "Последнее автоматическое перемещение должно отменяться.");
            Check(File.Exists(installerPath), "После отмены файл должен вернуться во «Входящие».");

            await AiWorkspaceDiscoveryRegressionTests.RunAsync(testRoot);
            await CatalogRegressionTests.RunAsync(testRoot);
            await FileOperationRegressionTests.RunAsync();

            Console.WriteLine("Nexus.Core.Tests: все проверки пройдены.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Nexus.Core.Tests: {exception.Message}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(testRoot)
                && testRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)
                && testRoot.Length > tempBase.Length + 10)
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string BencodeString(string value)
    {
        return $"{Encoding.UTF8.GetByteCount(value)}:{value}";
    }

    private static async Task CheckThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}

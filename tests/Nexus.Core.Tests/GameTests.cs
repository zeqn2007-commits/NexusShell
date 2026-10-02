using Microsoft.Win32;
using Nexus.Core.Games;

namespace Nexus.Core.Tests;

public sealed class GameTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _registryRoot = $@"Software\NexusTests\{Guid.NewGuid():N}";

    [Fact]
    public void ValveKeyValues_ReadsNestedBlocksCommentsAndEscapes()
    {
        var data = ValveKeyValues.Parse("""
            // comment
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "label" "" }
                "1"
                {
                    "path"  "D:\\SteamLibrary"
                }
            }
            """);

        var folders = data.Child("libraryfolders")!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.Child("0")!["path"]);
        Assert.Equal(@"D:\SteamLibrary", folders.Child("1")!["path"]);
        Assert.Equal(string.Empty, folders.Child("0")!["label"]);
    }

    [Fact]
    public void SteamLibrary_ReadsInstalledGamesFromEveryLibrary()
    {
        var steam = _temp.Folder("Steam");
        var second = _temp.Folder("SteamLibrary");
        _temp.File(@"Steam\steamapps\libraryfolders.vdf", $$"""
            "libraryfolders" { "0" { "path" "{{Escape(steam)}}" } "1" { "path" "{{Escape(second)}}" } }
            """);
        _temp.File(@"Steam\steamapps\appmanifest_620.acf", Manifest("620", "Portal 2", "Portal 2", flags: 4, lastPlayed: 1790526606, size: 13504412280));
        _temp.File(@"Steam\steamapps\appmanifest_228980.acf", Manifest("228980", "Steamworks Common Redistributables", "Steamworks Shared", flags: 4));
        _temp.File(@"Steam\steamapps\appmanifest_999.acf", Manifest("999", "Still Downloading", "Downloading", flags: 1026));
        _temp.File(@"SteamLibrary\steamapps\appmanifest_105600.acf", Manifest("105600", "Terraria", "Terraria", flags: 4));
        var cover = _temp.File(@"Steam\appcache\librarycache\620\library_600x900.jpg");
        var hero = _temp.File(@"Steam\appcache\librarycache\620\a58588d8\library_hero.jpg");

        var games = SteamLibrary.Load(steam).OrderBy(game => game.Title).ToArray();

        Assert.Equal(["Portal 2", "Terraria"], games.Select(game => game.Title));
        var portal = games[0];
        Assert.Equal("steam:620", portal.Id);
        Assert.Equal("steam://rungameid/620", portal.LaunchTarget);
        Assert.Equal(Path.Combine(steam, "steamapps", "common", "Portal 2"), portal.InstallPath);
        Assert.Equal(cover, portal.CoverPath);
        Assert.Equal(hero, portal.HeroPath);
        Assert.Equal(13504412280, portal.SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790526606), portal.LastPlayed);
        Assert.Equal(Path.Combine(second, "steamapps", "common", "Terraria"), games[1].InstallPath);
        Assert.Null(games[1].LastPlayed);
    }

    [Fact]
    public void SteamAppInfo_ReadsAppTypesFromTheBinaryCache()
    {
        var path = Path.Combine(_temp.Path, "appinfo.vdf");
        WriteAppInfo(path, (620, "Game"), (365670, "Application"), (105600, "game"));

        var types = SteamAppInfo.ReadTypes(path, ["620", "365670", "999"]);

        Assert.Equal("Game", types["620"]);
        Assert.Equal("Application", types["365670"]);
        Assert.False(types.ContainsKey("999"));
        Assert.True(SteamAppInfo.IsNotAGame(types["365670"]));
    }

    [Fact]
    public void SteamLibrary_LeavesProgramsOutOfTheGames()
    {
        var steam = _temp.Folder("Steam");
        _temp.File(@"Steam\steamapps\appmanifest_620.acf", Manifest("620", "Portal 2", "Portal 2", flags: 4));
        _temp.File(@"Steam\steamapps\appmanifest_365670.acf", Manifest("365670", "Blender", "Blender", flags: 4));
        WriteAppInfo(Path.Combine(steam, "appcache", "appinfo.vdf"), (620, "Game"), (365670, "Application"));

        var game = Assert.Single(SteamLibrary.Load(steam));

        Assert.Equal("Portal 2", game.Title);
    }

    [Fact]
    public void EpicLibrary_ReadsGamesAndSkipsEnginesAndIncompleteInstalls()
    {
        var manifests = _temp.Folder("Manifests");
        _temp.File(@"Manifests\a.item", """
            { "DisplayName": "Hades II", "InstallLocation": "D:\\Epic\\HadesII", "AppName": "Hades2",
              "CatalogNamespace": "ns", "CatalogItemId": "item", "InstallSize": 1234, "AppCategories": ["public", "games"] }
            """);
        _temp.File(@"Manifests\b.item", """
            { "DisplayName": "Unreal Engine", "InstallLocation": "D:\\Epic\\UE", "AppName": "UE_5", "AppCategories": ["engines"] }
            """);
        _temp.File(@"Manifests\c.item", """
            { "DisplayName": "Half Done", "InstallLocation": "D:\\Epic\\Half", "AppName": "Half", "bIsIncompleteInstall": true }
            """);

        var game = Assert.Single(EpicLibrary.Load(manifests));

        Assert.Equal("Hades II", game.Title);
        Assert.Equal("com.epicgames.launcher://apps/ns%3Aitem%3AHades2?action=launch&silent=true", game.LaunchTarget);
        Assert.Equal(1234, game.SizeBytes);
    }

    [Fact]
    public void XboxLibrary_ComputesPackageFamilyNamesLikeWindows()
    {
        Assert.Equal(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            XboxLibrary.PackageFamilyName("Microsoft.WindowsCalculator", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [Fact]
    public void XboxLibrary_ReadsMicrosoftGameConfig()
    {
        var root = _temp.Folder("XboxGames");
        _temp.File(@"XboxGames\Forza\Content\MicrosoftGame.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="Microsoft.SunriseBaseGame" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.0.0.0" />
              <ExecutableList><Executable Name="ForzaHorizon5.exe" Id="Game" /></ExecutableList>
              <ShellVisuals DefaultDisplayName="Forza Horizon 5" SplashScreenImage="Splash.png" />
            </Game>
            """);
        var splash = _temp.File(@"XboxGames\Forza\Content\Splash.png");

        var game = Assert.Single(XboxLibrary.Load([root]));

        Assert.Equal("Forza Horizon 5", game.Title);
        Assert.Equal(@"shell:AppsFolder\Microsoft.SunriseBaseGame_8wekyb3d8bbwe!Game", game.LaunchTarget);
        Assert.Equal(splash, game.HeroPath);
    }

    [Fact]
    public void LocalGameScanner_PicksTheGameExecutableInEachSubfolder()
    {
        var library = _temp.Folder("Games");
        Executable(@"Games\Cool Game\CoolGame.exe", 2_000_000);
        Executable(@"Games\Cool Game\unins000.exe", 1_500_000);
        Executable(@"Games\Cool Game\UnityCrashHandler64.exe", 1_500_000);
        _temp.File(@"Games\Cool Game\UnityPlayer.dll");
        _temp.Folder(@"Games\Cool Game\CoolGame_Data");
        _temp.File(@"Games\Manuals\readme.txt");

        var game = Assert.Single(LocalGameScanner.Load([library], TestContext.Current.CancellationToken));

        Assert.Equal("Cool Game", game.Title);
        Assert.EndsWith(@"Cool Game\CoolGame.exe", game.LaunchTarget);
        Assert.Equal(Path.Combine(library, "Cool Game"), game.InstallPath);
    }

    [Fact]
    public void LocalGameScanner_TreatsAGameFolderAsOneGame()
    {
        var folder = _temp.Folder("Hollow Knight");
        Executable(@"Hollow Knight\hollow_knight.exe", 1_000_000);
        _temp.File(@"Hollow Knight\UnityPlayer.dll");
        _temp.Folder(@"Hollow Knight\hollow_knight_Data\Managed");
        var cover = _temp.File(@"Hollow Knight\cover.jpg");

        var game = Assert.Single(LocalGameScanner.Load([folder], TestContext.Current.CancellationToken));

        Assert.Equal(folder, game.InstallPath);
        Assert.Equal(cover, game.CoverPath);
    }

    [Fact]
    public void GameLibrary_MergePrefersLaunchersAndDropsHiddenGames()
    {
        var steam = new GameEntry("steam:1", "Celeste", GameSource.Steam, @"D:\Games\Celeste", "steam://rungameid/1");
        var local = new GameEntry(@"local:D:\Games\Celeste", "Celeste", GameSource.Local, @"D:\Games\Celeste\", @"D:\Games\Celeste\Celeste.exe");
        var hidden = new GameEntry("steam:2", "Blender", GameSource.Steam, @"D:\Steam\Blender", "steam://rungameid/2");

        var games = GameLibrary.Merge([local, steam, hidden], ["steam:2"]);

        Assert.Equal(steam, Assert.Single(games));
    }

    [Fact]
    public void GogLibrary_ReadsInstalledGamesFromTheRegistry()
    {
        var folder = _temp.Folder("Witcher");
        var exe = Executable(@"Witcher\witcher3.exe", 100_000);
        using (var game = Registry.CurrentUser.CreateSubKey($@"{_registryRoot}\Games\1207664663"))
        {
            game.SetValue("gameName", "The Witcher 3");
            game.SetValue("path", folder);
            game.SetValue("exe", exe);
        }

        using var games = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Games")!;
        var entry = Assert.Single(GogLibrary.Load(games));

        Assert.Equal("gog:1207664663", entry.Id);
        Assert.Equal(exe, entry.LaunchTarget);
        Assert.False(entry.IsUri);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\NexusTests"))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                parent.Close();
                Registry.CurrentUser.DeleteSubKey(@"Software\NexusTests", throwOnMissingSubKey: false);
            }
        }

        _temp.Dispose();
    }

    private string Executable(string relativePath, int size)
    {
        var path = _temp.File(relativePath);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static string Escape(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    /// <summary>A minimal appinfo.vdf in Steam's version 29 layout: entries with binary KeyValues and a string table.</summary>
    private static void WriteAppInfo(string path, params (uint AppId, string Type)[] apps)
    {
        string[] keys = ["appinfo", "appid", "common", "name", "type"];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8);
        writer.Write(0x07564429u);
        writer.Write(1u);
        var tableOffsetPosition = stream.Position;
        writer.Write(0L);
        foreach (var (appId, type) in apps)
        {
            using var body = new MemoryStream();
            using var data = new BinaryWriter(body, System.Text.Encoding.UTF8);
            data.Write(new byte[60]);
            data.Write((byte)0x00);
            data.Write(0);
            data.Write((byte)0x02);
            data.Write(1);
            data.Write((int)appId);
            data.Write((byte)0x00);
            data.Write(2);
            data.Write((byte)0x01);
            data.Write(3);
            WriteCString(data, $"App {appId}");
            data.Write((byte)0x01);
            data.Write(4);
            WriteCString(data, type);
            data.Write((byte)0x08);
            data.Write((byte)0x08);
            data.Write((byte)0x08);
            data.Flush();
            writer.Write(appId);
            writer.Write((uint)body.Length);
            writer.Write(body.ToArray());
        }

        writer.Write(0u);
        var tableOffset = stream.Position;
        writer.Write((uint)keys.Length);
        foreach (var key in keys)
        {
            WriteCString(writer, key);
        }

        stream.Position = tableOffsetPosition;
        writer.Write(tableOffset);
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static string Manifest(string appId, string name, string installDir, int flags, long lastPlayed = 0, long size = 0) => $$"""
        "AppState"
        {
            "appid"		"{{appId}}"
            "name"		"{{name}}"
            "StateFlags"		"{{flags}}"
            "installdir"		"{{installDir}}"
            "LastPlayed"		"{{lastPlayed}}"
            "SizeOnDisk"		"{{size}}"
        }
        """;
}

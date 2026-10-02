using Nexus.Core.Settings;

namespace Nexus.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Settings_RoundTripThroughDisk()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        var store = new SettingsStore(path);
        Assert.True(store.Update(settings =>
        {
            settings.ShowHiddenItems = true;
            settings.SortField = SortField.Size;
            settings.Favorites.Add(@"C:\Work\plan.docx");
        }));

        var reloaded = new SettingsStore(path).Current;
        Assert.True(reloaded.ShowHiddenItems);
        Assert.Equal(SortField.Size, reloaded.SortField);
        Assert.Equal([@"C:\Work\plan.docx"], reloaded.Favorites);
    }

    [Fact]
    public void Settings_KeepSessionWindowAndLayoutChoices()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        new SettingsStore(path).Update(settings =>
        {
            settings.StartupPage = StartupPage.LastSession;
            settings.LastSessionTabs = ["home", @"folder:C:\Work"];
            settings.LastSessionSelectedTab = 1;
            settings.Window = new WindowPlacement { X = 10, Y = 20, Width = 1400, Height = 900, IsMaximized = true };
            settings.HiddenSidebarSections = ["games"];
            settings.HiddenHomeSections = ["ai"];
            settings.ConfirmRecycle = true;
            settings.ClassicContextMenu = true;
            settings.Material = WindowMaterial.Glass;
            settings.Transparency = 70;
        });

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal(StartupPage.LastSession, reloaded.StartupPage);
        Assert.Equal(["home", @"folder:C:\Work"], reloaded.LastSessionTabs);
        Assert.Equal(1, reloaded.LastSessionSelectedTab);
        Assert.Equal((10, 20, 1400, 900, true), (reloaded.Window!.X, reloaded.Window.Y, reloaded.Window.Width, reloaded.Window.Height, reloaded.Window.IsMaximized));
        Assert.Equal(["games"], reloaded.HiddenSidebarSections);
        Assert.Equal(["ai"], reloaded.HiddenHomeSections);
        Assert.True(reloaded.ConfirmRecycle);
        Assert.True(reloaded.ClassicContextMenu);
        Assert.Equal(WindowMaterial.Glass, reloaded.Material);
        Assert.Equal(70, reloaded.Transparency);
        Assert.Contains("\"StartupPage\": \"LastSession\"", File.ReadAllText(path));
    }

    [Fact]
    public void ResetPreferences_RestoresDefaultsButKeepsWhatTheUserCollected()
    {
        var settings = new AppSettings
        {
            Theme = ThemePreference.Dark,
            Material = WindowMaterial.Glass,
            Transparency = 90,
            ShowHiddenItems = true,
            SortField = SortField.Size,
            SortDescending = true,
            StartupPage = StartupPage.ThisPc,
            HiddenSidebarSections = ["games"],
            ConfirmRecycle = true,
            Favorites = [@"C:\a.txt"],
            PinnedFolders = [@"C:\Work"],
            GameFolders = [@"D:\Games"],
            HiddenGames = ["steam:1"],
            AiFolders = [@"D:\AI"],
            LastSessionTabs = ["home"]
        };

        settings.ResetPreferences();

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(WindowMaterial.Standard, settings.Material);
        Assert.Equal(50, settings.Transparency);
        Assert.False(settings.ShowHiddenItems);
        Assert.Equal(SortField.Name, settings.SortField);
        Assert.False(settings.SortDescending);
        Assert.Equal(StartupPage.Home, settings.StartupPage);
        Assert.Empty(settings.HiddenSidebarSections);
        Assert.False(settings.ConfirmRecycle);
        Assert.Equal([@"C:\a.txt"], settings.Favorites);
        Assert.Equal([@"C:\Work"], settings.PinnedFolders);
        Assert.Equal([@"D:\Games"], settings.GameFolders);
        Assert.Equal(["steam:1"], settings.HiddenGames);
        Assert.Equal([@"D:\AI"], settings.AiFolders);
        Assert.Equal(["home"], settings.LastSessionTabs);
    }

    [Fact]
    public void GameLibrary_AddsAndRemovesGameFolders()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var library = new Nexus.Core.Games.GameLibrary(store);
        var folder = temp.Folder("Games");

        Assert.True(library.AddFolder(folder));
        Assert.False(library.AddFolder(folder + "\\"));
        Assert.Equal([folder], library.Folders);

        library.RemoveFolder(folder.ToUpperInvariant());

        Assert.Empty(library.Folders);
    }

    [Fact]
    public void Settings_BrokenFileFallsBackToDefaultsAndKeepsCopy()
    {
        using var temp = new TempFolder();
        var path = temp.File("settings.json", "{ not json");

        var settings = new SettingsStore(path).Current;

        Assert.False(settings.ShowHiddenItems);
        Assert.True(File.Exists(path + ".broken"));
    }
}

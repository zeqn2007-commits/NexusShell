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
    public void Settings_BrokenFileFallsBackToDefaultsAndKeepsCopy()
    {
        using var temp = new TempFolder();
        var path = temp.File("settings.json", "{ not json");

        var settings = new SettingsStore(path).Current;

        Assert.False(settings.ShowHiddenItems);
        Assert.True(File.Exists(path + ".broken"));
    }
}

using System.IO.Compression;
using Microsoft.Win32;
using Nexus.Core.Apps;
using Nexus.Core.Organizer;

namespace Nexus.Core.Tests;

public sealed class OrganizerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private readonly TempFolder _temp = new();

    [Fact]
    public void Categorize_SortsByTypeAndRecognisesAiFilesFirst()
    {
        File.WriteAllBytes(_temp.File("model.gguf"), new byte[2 * 1024 * 1024]);
        var skill = Path.Combine(_temp.Path, "skill.zip");
        using (var archive = ZipFile.Open(skill, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("pdf/SKILL.md").Open());
            writer.Write("---\nname: pdf\n---");
        }

        Assert.Equal(DownloadCategory.Documents, DownloadsOrganizer.Categorize(_temp.File("report.pdf")));
        Assert.Equal(DownloadCategory.Images, DownloadsOrganizer.Categorize(_temp.File("photo.JPG")));
        Assert.Equal(DownloadCategory.Video, DownloadsOrganizer.Categorize(_temp.File("movie.mkv")));
        Assert.Equal(DownloadCategory.Music, DownloadsOrganizer.Categorize(_temp.File("song.flac")));
        Assert.Equal(DownloadCategory.Archives, DownloadsOrganizer.Categorize(_temp.File("data.7z")));
        Assert.Equal(DownloadCategory.Programs, DownloadsOrganizer.Categorize(_temp.File("setup.exe")));
        Assert.Equal(DownloadCategory.Torrents, DownloadsOrganizer.Categorize(_temp.File("linux.torrent")));
        Assert.Equal(DownloadCategory.Ai, DownloadsOrganizer.Categorize(Path.Combine(_temp.Path, "model.gguf")));
        Assert.Equal(DownloadCategory.Ai, DownloadsOrganizer.Categorize(_temp.File("review.prompt.md")));
        Assert.Equal(DownloadCategory.Ai, DownloadsOrganizer.Categorize(skill));
        Assert.Equal(DownloadCategory.Other, DownloadsOrganizer.Categorize(_temp.File("thing.xyz")));
    }

    [Fact]
    public void Scan_TakesSettledFilesOnly()
    {
        var downloads = _temp.Folder("Downloads");
        Old(_temp.File(@"Downloads\book.pdf"));
        Old(_temp.File(@"Downloads\film.mkv.crdownload"));
        Old(_temp.File(@"Downloads\video.part"));
        _temp.File(@"Downloads\fresh.zip");
        var hidden = Old(_temp.File(@"Downloads\desktop.ini"));
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        _temp.File(@"Downloads\folder\inside.pdf");

        var items = DownloadsOrganizer.Scan(downloads, Now);

        var item = Assert.Single(items);
        Assert.Equal("book.pdf", item.Name);
        Assert.Equal(DownloadCategory.Documents, item.Category);
    }

    [Fact]
    public void FindExtractedArchives_NeedsAFolderWithContentNextToIt()
    {
        var downloads = _temp.Folder("Downloads");
        _temp.File(@"Downloads\photos\a.jpg");
        _temp.Folder(@"Downloads\empty");
        _temp.File(@"Downloads\release\readme.txt");
        Old(_temp.File(@"Downloads\photos.zip"));
        Old(_temp.File(@"Downloads\empty.zip"));
        Old(_temp.File(@"Downloads\lonely.rar"));
        Old(_temp.File(@"Downloads\release.tar.gz"));

        var suggestions = DownloadsOrganizer.FindExtractedArchives(DownloadsOrganizer.Scan(downloads, Now));

        Assert.Equal(["photos.zip", "release.tar.gz"], suggestions.Select(suggestion => suggestion.Item.Name).Order());
        Assert.Equal("photos", suggestions.Single(suggestion => suggestion.Item.Name == "photos.zip").Related);
        Assert.All(suggestions, suggestion => Assert.Equal(CleanupReason.ExtractedArchive, suggestion.Reason));
    }

    [Fact]
    public void FindDuplicates_ComparesContentOfCopyNamedFiles()
    {
        var downloads = _temp.Folder("Downloads");
        Old(_temp.File(@"Downloads\report.pdf", "same"));
        Old(_temp.File(@"Downloads\report (1).pdf", "same"));
        Old(_temp.File(@"Downloads\notes.txt", "text"));
        Old(_temp.File(@"Downloads\notes — копия.txt", "text"));
        Old(_temp.File(@"Downloads\plan.txt", "one"));
        Old(_temp.File(@"Downloads\plan (2).txt", "two"));
        _temp.File(@"Downloads\Документы\invoice.pdf", "bill");
        Old(_temp.File(@"Downloads\invoice (1).pdf", "bill"));

        var suggestions = DownloadsOrganizer.FindDuplicates(DownloadsOrganizer.Scan(downloads, Now));

        Assert.Equal(
            ["invoice (1).pdf", "notes — копия.txt", "report (1).pdf"],
            suggestions.Select(suggestion => suggestion.Item.Name).Order(StringComparer.Ordinal));
        Assert.Equal("report.pdf", suggestions.Single(suggestion => suggestion.Item.Name == "report (1).pdf").Related);
        Assert.Equal("Документы › invoice.pdf", suggestions.Single(suggestion => suggestion.Item.Name == "invoice (1).pdf").Related);
    }

    [Fact]
    public void FindInstalledInstallers_MatchesProductNamesWithInstalledPrograms()
    {
        var downloads = _temp.Folder("Downloads");
        foreach (var name in new[] { "AnyDesk.exe", "td-setup-win-x64-7.2.9.exe", "7z2408-x64.exe", "SteamSetup.exe", "mystery.exe" })
        {
            Old(_temp.File($@"Downloads\{name}"));
        }

        var products = new Dictionary<string, string?>
        {
            ["AnyDesk.exe"] = "AnyDesk",
            ["td-setup-win-x64-7.2.9.exe"] = "Telegram Desktop",
            ["7z2408-x64.exe"] = "7-Zip",
            ["SteamSetup.exe"] = "Steam",
            ["mystery.exe"] = null
        };
        string[] installed = ["Steamworks Common Redistributables", "Steam", "AnyDesk", "Telegram Desktop", "7-Zip 24.08 (x64)"];

        var suggestions = DownloadsOrganizer.FindInstalledInstallers(
            DownloadsOrganizer.Scan(downloads, Now), installed, path => products[Path.GetFileName(path)]);

        Assert.Equal(
            [("7z2408-x64.exe", "7-Zip 24.08 (x64)"), ("AnyDesk.exe", "AnyDesk"), ("SteamSetup.exe", "Steam"), ("td-setup-win-x64-7.2.9.exe", "Telegram Desktop")],
            suggestions.Select(suggestion => (suggestion.Item.Name, suggestion.Related)).OrderBy(pair => pair.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void RemoveEmptyCategoryFolders_LeavesFoldersWithContentAndOtherFolders()
    {
        var downloads = _temp.Folder("Downloads");
        _temp.Folder(@"Downloads\Документы");
        _temp.File(@"Downloads\Видео\clip.mp4");
        _temp.Folder(@"Downloads\Мои вещи");

        DownloadsOrganizer.RemoveEmptyCategoryFolders(downloads);

        Assert.False(Directory.Exists(Path.Combine(downloads, "Документы")));
        Assert.True(Directory.Exists(Path.Combine(downloads, "Видео")));
        Assert.True(Directory.Exists(Path.Combine(downloads, "Мои вещи")));
    }

    [Theory]
    [InlineData("7-Zip 24.08 (x64)", "7zip")]
    [InlineData("Telegram Desktop", "telegramdesktop")]
    [InlineData("Microsoft Edge Update Setup", "microsoftedge")]
    [InlineData("VLC media player 3.0.21", "vlcmediaplayer")]
    [InlineData("Setup", "")]
    public void ProductKey_KeepsTheMeaningfulWords(string name, string key) => Assert.Equal(key, DownloadsOrganizer.ProductKey(name));

    [Fact]
    public void InstalledPrograms_ReadsDisplayNamesOfUninstallEntries()
    {
        var path = $@"Software\NexusTests\{Guid.NewGuid():N}\Uninstall";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                using (var app = key.CreateSubKey("{A}"))
                {
                    app.SetValue("DisplayName", "Telegram Desktop");
                }

                using (var nameless = key.CreateSubKey("Component"))
                {
                    nameless.SetValue("Version", "1");
                }
            }

            Assert.Equal(["Telegram Desktop"], InstalledPrograms.Read(Registry.CurrentUser, path));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path[..path.LastIndexOf('\\')], throwOnMissingSubKey: false);
        }
    }

    public void Dispose() => _temp.Dispose();

    private static string Old(string path)
    {
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-3));
        return path;
    }
}

using Nexus.Core.IO;

namespace Nexus.Core.Tests;

public sealed class IoTests
{
    [Fact]
    public void PathHelper_RecognisesNetworkComputers()
    {
        Assert.True(PathHelper.IsNetworkComputer(@"\\NAS"));
        Assert.True(PathHelper.IsNetworkComputer(@"\\NAS\"));
        Assert.False(PathHelper.IsNetworkComputer(@"\\NAS\Media"));
        Assert.False(PathHelper.IsNetworkComputer(@"C:\Users"));
        Assert.False(PathHelper.IsNetworkComputer(@"\\"));
    }

    [Fact]
    public void PathHelper_WalksUpFromSharesToTheirComputer()
    {
        Assert.Equal(@"\\NAS\Media", PathHelper.GetParent(@"\\NAS\Media\Films"));
        Assert.Equal(@"\\NAS", PathHelper.GetParent(@"\\NAS\Media"));
        Assert.Null(PathHelper.GetParent(@"\\NAS"));
        Assert.Null(PathHelper.GetParent(@"C:\"));
        Assert.Equal(@"C:\", PathHelper.GetParent(@"C:\Users"));
    }

    [Fact]
    public void DirectoryReader_HidesHiddenAndProtectedFilesByDefault()
    {
        using var temp = new TempFolder();
        temp.File("visible.txt");
        var hidden = temp.File("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = temp.File("protected.sys");
        File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);
        temp.File("desktop.ini");
        temp.Folder("Subfolder");

        var names = DirectoryReader.Read(temp.Path, new DirectoryReadOptions(), TestContext.Current.CancellationToken).Select(entry => entry.Name).Order().ToArray();

        Assert.Equal(["Subfolder", "visible.txt"], names);
    }

    [Fact]
    public void DirectoryReader_ShowsHiddenButNotProtectedWhenAsked()
    {
        using var temp = new TempFolder();
        var hidden = temp.File("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = temp.File("protected.sys");
        File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);

        var names = DirectoryReader.Read(temp.Path, new DirectoryReadOptions(ShowHidden: true), TestContext.Current.CancellationToken).Select(entry => entry.Name).ToArray();

        Assert.Equal(["hidden.txt"], names);
    }

    [Fact]
    public void DirectoryReader_ReportsSizesAndKinds()
    {
        using var temp = new TempFolder();
        temp.File("data.bin", new string('x', 1234));
        temp.Folder("Folder");

        var entries = DirectoryReader.Read(temp.Path, new DirectoryReadOptions(), TestContext.Current.CancellationToken).ToDictionary(entry => entry.Name);

        Assert.Equal(1234, entries["data.bin"].Size);
        Assert.False(entries["data.bin"].IsDirectory);
        Assert.True(entries["Folder"].IsDirectory);
        Assert.Equal(".bin", entries["data.bin"].Extension);
    }

    [Fact]
    public void DirectoryReader_MissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            DirectoryReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new DirectoryReadOptions(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(@"C:\Users\Test\", @"C:\Users\Test")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Folder\..\Other", @"C:\Other")]
    public void PathHelper_Normalize(string input, string expected) =>
        Assert.Equal(expected, PathHelper.Normalize(input));

    [Fact]
    public void PathHelper_IsInsideIsStrictAndBoundaryAware()
    {
        Assert.True(PathHelper.IsInside(@"C:\a\b", @"C:\a"));
        Assert.False(PathHelper.IsInside(@"C:\a", @"C:\a"));
        Assert.False(PathHelper.IsInside(@"C:\ab", @"C:\a"));
    }

    [Fact]
    public void PathHelper_GetAvailableNameSkipsExisting()
    {
        using var temp = new TempFolder();
        temp.Folder("Новая папка");
        temp.Folder("Новая папка (2)");

        Assert.Equal("Новая папка (3)", PathHelper.GetAvailableName(temp.Path, "Новая папка"));
        Assert.Equal("Документ.txt", PathHelper.GetAvailableName(temp.Path, "Документ", ".txt"));
    }

    [Theory]
    [InlineData("Отчёт.docx", true)]
    [InlineData("", false)]
    [InlineData("a:b", false)]
    [InlineData("name.", false)]
    [InlineData(" name", false)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    [InlineData("console.txt", true)]
    public void FileNameValidator_Rules(string name, bool valid) =>
        Assert.Equal(valid, FileNameValidator.Validate(name) is null);

    [Fact]
    public async Task FileSearch_FindsNestedMatchesAndSkipsReparsePoints()
    {
        using var temp = new TempFolder();
        temp.File(@"a\отчёт за май.docx");
        temp.File(@"a\b\Отчёт за июнь.docx");
        temp.File(@"a\b\прочее.txt");

        var results = new List<string>();
        await foreach (var entry in FileSearch.SearchAsync(temp.Path, "отчёт", new DirectoryReadOptions(), cancellationToken: TestContext.Current.CancellationToken))
        {
            results.Add(entry.Name);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, name => Assert.Contains("тчёт", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FileSearch_AllTermsMustMatch()
    {
        Assert.True(FileSearch.Matches("Отчёт за май.docx", ["отчёт", "май"]));
        Assert.False(FileSearch.Matches("Отчёт за июнь.docx", ["отчёт", "май"]));
    }

    [Fact]
    public void KnownFolders_ResolveExistingDocumentsFolder()
    {
        var documents = KnownFolders.GetPath(KnownFolder.Documents);
        Assert.False(string.IsNullOrWhiteSpace(documents));
        Assert.Equal("Документы", KnownFolders.Find(documents)?.Title);
    }
}

using Nexus.Core.Operations;

namespace Nexus.Core.Tests;

/// <summary>
/// Copy/move go through the real Windows engine in silent mode, inside temp folders only.
/// Recycling is deliberately not exercised: it would touch the user's Recycle Bin.
/// </summary>
public sealed class FileOperationTests
{
    private readonly FileOperationService _service = new() { Silent = true };

    [Fact]
    public async Task Copy_CopiesFilesAndFoldersAndKeepsSource()
    {
        using var temp = new TempFolder();
        var file = temp.File(@"src\report.txt", "hello");
        temp.File(@"src\nested\inner.txt", "inner");
        var destination = temp.Folder("dst");

        var outcome = await _service.CopyAsync([file, Path.Combine(temp.Path, "src", "nested")], destination, IntPtr.Zero);

        Assert.True(outcome.Completed, outcome.Error);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "report.txt")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(destination, "nested", "inner.txt")));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Move_MovesAndRemovesSource()
    {
        using var temp = new TempFolder();
        var file = temp.File(@"src\report.txt", "hello");
        var destination = temp.Folder("dst");

        var outcome = await _service.MoveAsync([file], destination, IntPtr.Zero);

        Assert.True(outcome.Completed, outcome.Error);
        Assert.False(File.Exists(file));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "report.txt")));
        Assert.Contains(outcome.CreatedPaths, path => path.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Copy_IntoItselfIsRefusedBeforeWindowsIsCalled()
    {
        using var temp = new TempFolder();
        var folder = temp.Folder("parent");
        var child = temp.Folder(@"parent\child");

        var outcome = await _service.CopyAsync([folder], child, IntPtr.Zero);

        Assert.False(outcome.Completed);
        Assert.Contains("саму себя", outcome.Error);
    }

    [Fact]
    public async Task Move_IntoSameFolderIsRefused()
    {
        using var temp = new TempFolder();
        var file = temp.File("a.txt");

        var outcome = await _service.MoveAsync([file], temp.Path, IntPtr.Zero);

        Assert.False(outcome.Completed);
        Assert.NotNull(outcome.Error);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Copy_MissingSourceReportsErrorAndChangesNothing()
    {
        using var temp = new TempFolder();
        var destination = temp.Folder("dst");

        var outcome = await _service.CopyAsync([Path.Combine(temp.Path, "missing.txt")], destination, IntPtr.Zero);

        Assert.False(outcome.Completed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void Rename_RenamesAndRejectsCollisions()
    {
        using var temp = new TempFolder();
        var file = temp.File("old.txt");
        temp.File("taken.txt");

        var renamed = FileOperationService.Rename(file, "new.txt");

        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(file));
        Assert.Throws<IOException>(() => FileOperationService.Rename(renamed, "taken.txt"));
        Assert.Throws<ArgumentException>(() => FileOperationService.Rename(renamed, "bad|name.txt"));
    }

    [Fact]
    public void Rename_CaseOnlyChangeWorksForFolders()
    {
        using var temp = new TempFolder();
        var folder = temp.Folder("projects");

        var renamed = FileOperationService.Rename(folder, "Projects");

        Assert.Equal("Projects", new DirectoryInfo(renamed).Name);
        Assert.Single(Directory.EnumerateDirectories(temp.Path));
        Assert.Equal("Projects", Path.GetFileName(Directory.EnumerateDirectories(temp.Path).Single()));
    }

    [Fact]
    public void CreateFolder_RefusesExistingName()
    {
        using var temp = new TempFolder();
        var created = FileOperationService.CreateFolder(temp.Path, "Новая папка");

        Assert.True(Directory.Exists(created));
        Assert.Throws<IOException>(() => FileOperationService.CreateFolder(temp.Path, "Новая папка"));
    }
}

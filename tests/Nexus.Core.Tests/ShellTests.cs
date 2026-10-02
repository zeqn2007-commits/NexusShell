using Nexus.Core.Shell;

namespace Nexus.Core.Tests;

public sealed class ShellTests
{
    [Fact]
    public void ShellContextMenu_AcceptsItemsOfOneFolder()
    {
        Assert.True(ShellContextMenu.CanShowFor([@"C:\Work\a.txt", @"C:\Work\b.txt", @"c:\work\Docs"]));
    }

    [Fact]
    public void ShellContextMenu_RejectsItemsFromDifferentFolders()
    {
        // Search results can mix folders; Windows builds one menu per folder only.
        Assert.False(ShellContextMenu.CanShowFor([@"C:\Work\a.txt", @"C:\Work\Docs\b.txt"]));
    }

    [Fact]
    public void ShellContextMenu_TreatsDrivesAsSiblings()
    {
        Assert.True(ShellContextMenu.CanShowFor([@"C:\", @"D:\"]));
        Assert.False(ShellContextMenu.CanShowFor([@"C:\", @"C:\Work"]));
    }

    [Fact]
    public void ShellContextMenu_NeedsAtLeastOneItem()
    {
        Assert.False(ShellContextMenu.CanShowFor([]));
    }
}

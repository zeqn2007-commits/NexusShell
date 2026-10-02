using Microsoft.Win32;
using Nexus.Core.Integration;

namespace Nexus.Core.Tests;

public sealed class LaunchRequestTests
{
    [Fact]
    public void Parse_FolderPath()
    {
        using var temp = new TempFolder();
        var request = LaunchRequest.Parse([temp.Path]);
        Assert.Equal(LaunchTarget.Folder, request.Target);
        Assert.Equal(temp.Path, request.Path);
    }

    [Fact]
    public void Parse_SelectWithAndWithoutQuotes()
    {
        using var temp = new TempFolder();
        var file = temp.File("отчёт за май.docx");

        Assert.Equal(new LaunchRequest(LaunchTarget.SelectItem, file), LaunchRequest.Parse([$"/select,{file}"]));
        Assert.Equal(new LaunchRequest(LaunchTarget.SelectItem, file), LaunchRequest.Parse([$"/select,\"{file}\""]));
        Assert.Equal(new LaunchRequest(LaunchTarget.SelectItem, file), LaunchRequest.Parse(["/select", file]));
    }

    [Fact]
    public void Parse_ExplorerPrefixesAndShellFolders()
    {
        using var temp = new TempFolder();
        Assert.Equal(LaunchTarget.Folder, LaunchRequest.Parse([$"/e,{temp.Path}"]).Target);
        Assert.Equal("thispc", LaunchRequest.Parse(["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"]).Section);
        Assert.Equal("recycle", LaunchRequest.Parse(["shell:RecycleBinFolder"]).Section);
    }

    [Fact]
    public void Parse_NetworkComputerOpensItsShares()
    {
        Assert.Equal(new LaunchRequest(LaunchTarget.Folder, @"\\NAS"), LaunchRequest.Parse([@"\\NAS\"]));
    }

    [Fact]
    public void Parse_IgnoresNexusOptionsAndUnknownPaths()
    {
        Assert.Equal(LaunchTarget.Default, LaunchRequest.Parse(["--page=games", "--theme=dark"]).Target);
        Assert.Equal(LaunchTarget.Default, LaunchRequest.Parse([@"Z:\definitely\missing\folder"]).Target);
    }

    [Fact]
    public void SplitCommandLine_RespectsQuotes()
    {
        var parts = LaunchRequest.SplitCommandLine("\"C:\\Program Files\\Nexus\\Nexus.exe\" \"C:\\Мои документы\" --page=home");
        Assert.Equal(["C:\\Program Files\\Nexus\\Nexus.exe", "C:\\Мои документы", "--page=home"], parts);
    }
}

/// <summary>Registry tests run against an isolated HKCU\Software\NexusTests key that is deleted afterwards.</summary>
public sealed class DefaultFileManagerTests : IDisposable
{
    private readonly string _testRoot = $@"Software\NexusTests\{Guid.NewGuid():N}";
    private readonly TempFolder _temp = new();

    private string Classes => $@"{_testRoot}\Classes";

    [Fact]
    public void Enable_RegistersFoldersDrivesAndWinE_ThenDisableRestoresPreviousVerb()
    {
        using (var shell = Registry.CurrentUser.CreateSubKey($@"{Classes}\Directory\shell"))
        {
            shell.SetValue(null, "previousVerb");
        }

        var exe = _temp.File("Nexus.exe", "stub");
        var manager = new DefaultFileManager(Classes, Path.Combine(_temp.Path, "backup.json"));

        manager.Enable(exe);

        Assert.True(manager.IsEnabled());
        Assert.Equal(exe, manager.RegisteredExecutable());
        Assert.Equal($"\"{exe}\" \"%1\"", Read($@"{Classes}\Drive\shell\nexus\command"));
        Assert.Equal($"\"{exe}\"", Read($@"{Classes}\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}}\shell\opennewwindow\command"));

        manager.Disable();

        Assert.False(manager.IsEnabled());
        Assert.Equal("previousVerb", Read($@"{Classes}\Directory\shell"));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{Classes}\Directory\shell\nexus"));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{Classes}\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}}"));
    }

    [Fact]
    public void Enable_RefusesMissingExecutable()
    {
        var manager = new DefaultFileManager(Classes, Path.Combine(_temp.Path, "backup.json"));
        Assert.Throws<FileNotFoundException>(() => manager.Enable(Path.Combine(_temp.Path, "missing.exe")));
        Assert.False(manager.IsEnabled());
    }

    [Fact]
    public void Enable_TwiceKeepsOriginalBackup()
    {
        var exe = _temp.File("Nexus.exe", "stub");
        var manager = new DefaultFileManager(Classes, Path.Combine(_temp.Path, "backup.json"));
        manager.Enable(exe);
        manager.Enable(exe);
        manager.Disable();

        Assert.Null(Read($@"{Classes}\Directory\shell"));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testRoot, throwOnMissingSubKey: false);
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

    private static string? Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }
}

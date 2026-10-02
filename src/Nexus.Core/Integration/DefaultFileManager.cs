using System.Text.Json;
using Microsoft.Win32;

namespace Nexus.Core.Integration;

/// <summary>
/// Makes Nexus open folders instead of File Explorer, for the current user only
/// (HKCU\Software\Classes). Explorer keeps running the taskbar, Start and desktop.
/// Previous values are saved so <see cref="Disable"/> restores the exact prior state.
/// </summary>
public sealed class DefaultFileManager
{
    private const string VerbName = "nexus";
    private const string FileExplorerHomeClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    private readonly RegistryKey _root;
    private readonly string _classesPath;
    private readonly string _backupPath;

    /// <param name="classesPath">Registry path under HKCU; tests pass an isolated key.</param>
    /// <param name="backupPath">Where the previous values are kept for restoring.</param>
    public DefaultFileManager(string? classesPath = null, string? backupPath = null, RegistryKey? root = null)
    {
        _root = root ?? Registry.CurrentUser;
        _classesPath = classesPath ?? @"Software\Classes";
        _backupPath = backupPath ?? Path.Combine(Settings.SettingsStore.DataDirectory, "explorer-defaults-backup.json");
    }

    /// <summary>True when folders and drives currently open in Nexus.</summary>
    public bool IsEnabled()
    {
        using var shell = _root.OpenSubKey($@"{_classesPath}\Directory\shell");
        return string.Equals(shell?.GetValue(null) as string, VerbName, StringComparison.Ordinal);
    }

    /// <summary>The executable currently registered, if any.</summary>
    public string? RegisteredExecutable()
    {
        using var command = _root.OpenSubKey($@"{_classesPath}\Directory\shell\{VerbName}\command");
        var value = command?.GetValue(null) as string;
        if (string.IsNullOrEmpty(value) || value[0] != '"')
        {
            return null;
        }

        var end = value.IndexOf('"', 1);
        return end > 1 ? value[1..end] : null;
    }

    public void Enable(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Исполняемый файл Nexus не найден.", executablePath);
        }

        if (!IsEnabled())
        {
            SaveBackup();
        }

        foreach (var type in new[] { "Directory", "Drive" })
        {
            using (var shell = _root.CreateSubKey($@"{_classesPath}\{type}\shell"))
            {
                shell.SetValue(null, VerbName);
            }

            using (var verb = _root.CreateSubKey($@"{_classesPath}\{type}\shell\{VerbName}"))
            {
                verb.SetValue(null, "Открыть в Nexus");
                verb.SetValue("Icon", $"\"{executablePath}\",0");
            }

            using var command = _root.CreateSubKey($@"{_classesPath}\{type}\shell\{VerbName}\command");
            command.SetValue(null, $"\"{executablePath}\" \"%1\"");
        }

        // Win+E and the File Explorer taskbar/Start entry open Nexus.
        using var home = _root.CreateSubKey($@"{_classesPath}\CLSID\{FileExplorerHomeClsid}\shell\opennewwindow\command");
        home.SetValue(null, $"\"{executablePath}\"");
        home.SetValue("DelegateExecute", string.Empty);
    }

    /// <summary>Removes everything Nexus added and restores the saved previous defaults.</summary>
    public void Disable()
    {
        var backup = LoadBackup();
        foreach (var type in new[] { "Directory", "Drive" })
        {
            _root.DeleteSubKeyTree($@"{_classesPath}\{type}\shell\{VerbName}", throwOnMissingSubKey: false);
            using var shell = _root.OpenSubKey($@"{_classesPath}\{type}\shell", writable: true);
            if (shell is null)
            {
                continue;
            }

            if (backup.DefaultVerbs.TryGetValue(type, out var previous) && previous is not null)
            {
                shell.SetValue(null, previous);
            }
            else if (string.Equals(shell.GetValue(null) as string, VerbName, StringComparison.Ordinal))
            {
                shell.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }

        if (!backup.HadExplorerHomeOverride)
        {
            _root.DeleteSubKeyTree($@"{_classesPath}\CLSID\{FileExplorerHomeClsid}", throwOnMissingSubKey: false);
        }

        if (File.Exists(_backupPath))
        {
            File.Delete(_backupPath);
        }
    }

    private void SaveBackup()
    {
        var backup = new Backup();
        foreach (var type in new[] { "Directory", "Drive" })
        {
            using var shell = _root.OpenSubKey($@"{_classesPath}\{type}\shell");
            backup.DefaultVerbs[type] = shell?.GetValue(null) as string;
        }

        using (var home = _root.OpenSubKey($@"{_classesPath}\CLSID\{FileExplorerHomeClsid}"))
        {
            backup.HadExplorerHomeOverride = home is not null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllText(_backupPath, JsonSerializer.Serialize(backup));
    }

    private Backup LoadBackup()
    {
        try
        {
            return File.Exists(_backupPath)
                ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(_backupPath)) ?? new Backup()
                : new Backup();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Backup();
        }
    }

    private sealed class Backup
    {
        public Dictionary<string, string?> DefaultVerbs { get; set; } = [];

        public bool HadExplorerHomeOverride { get; set; }
    }
}

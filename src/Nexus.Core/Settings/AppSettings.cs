using System.Text.Json.Serialization;

namespace Nexus.Core.Settings;

public enum SortField
{
    Name,
    Modified,
    Type,
    Size
}

public enum FolderViewMode
{
    Details,
    Tiles
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>Everything Nexus remembers between launches.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public bool ShowHiddenItems { get; set; }

    public bool ShowFileExtensions { get; set; } = true;

    public bool ShowDetailsPane { get; set; } = true;

    public FolderViewMode ViewMode { get; set; } = FolderViewMode.Details;

    public SortField SortField { get; set; } = SortField.Name;

    public bool SortDescending { get; set; }

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Files and folders starred by the user.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Folders pinned to quick access in addition to the Windows known folders.</summary>
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>Extra folders scanned for portable/repack games.</summary>
    public List<string> GameFolders { get; set; } = [];

    /// <summary>Install folders the user hid from the game library.</summary>
    public List<string> HiddenGames { get; set; } = [];

    /// <summary>Extra roots scanned for AI projects.</summary>
    public List<string> AiFolders { get; set; } = [];

    public bool ShowSystemApps { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

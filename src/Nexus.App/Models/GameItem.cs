namespace Nexus.App.Models;

public enum GameSource
{
    Steam,
    Epic,
    Xbox,
    Local
}

public sealed class GameItem
{
    // Not "required": the XAML type registry needs a parameterless activation path
    // because GameItem is the type of GameCover.Game.
    public string Name { get; init; } = string.Empty;

    public GameSource Source { get; init; } = GameSource.Local;

    public string InstallPath { get; init; } = string.Empty;

    public string? CoverPath { get; init; }

    public string? HeroPath { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>Gradient colours for the generated cover when no artwork exists.</summary>
    public string PaletteFrom { get; init; } = "#FF3A4A6B";

    public string PaletteTo { get; init; } = "#FF1E2638";

    public string SourceLabel => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Xbox => "Xbox",
        _ => "Локальная"
    };

    public string Monogram => string.Concat(Name
        .Split([' ', ':', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Take(2)
        .Select(word => char.ToUpperInvariant(word[0])));

    public string DisplaySize => Formatting.Size(SizeBytes);

    public string DisplayLastPlayed => LastPlayed is { } value
        ? Formatting.RelativeDate(value, DateTimeOffset.Now)
        : "Ещё не запускалась";

    public bool HasCover => CoverPath is not null && File.Exists(CoverPath);

    public bool HasHero => HeroPath is not null && File.Exists(HeroPath);
}

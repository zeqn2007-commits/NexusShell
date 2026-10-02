using Nexus.Core.Games;

namespace Nexus.App.Models;

public sealed class GameItem
{
    // Generated covers: a game keeps its gradient across runs because it is picked by title.
    private static readonly (string From, string To)[] Palettes =
    [
        ("#FF3A4A6B", "#FF1E2638"), ("#FF2B3A55", "#FF0E1420"), ("#FF7A2E3B", "#FF2A0F18"), ("#FF1F6B7A", "#FF0B2730"),
        ("#FF3F7A3A", "#FF15301A"), ("#FF6A3F8F", "#FF231536"), ("#FF8A5A2B", "#FF2E1C0C"), ("#FF2F5F8F", "#FF0F2236")
    ];

    // Not "required": the XAML type registry needs a parameterless activation path
    // because GameItem is the type of GameCover.Game.
    public GameEntry? Entry { get; init; }

    public string Name { get; init; } = string.Empty;

    public GameSource Source { get; init; } = GameSource.Local;

    public string InstallPath { get; init; } = string.Empty;

    public string? CoverPath { get; init; }

    public string? HeroPath { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>Gradient colours for the generated cover when no artwork exists.</summary>
    public string PaletteFrom { get; init; } = Palettes[0].From;

    public string PaletteTo { get; init; } = Palettes[0].To;

    public bool CanLaunch => Entry?.LaunchTarget is not null;

    public string SourceLabel => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Xbox => "Xbox",
        GameSource.Gog => "GOG",
        _ => "Локальная"
    };

    public string Monogram => string.Concat(Name
        .Split([' ', ':', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Take(2)
        .Select(word => char.ToUpperInvariant(word[0])));

    public string DisplaySize => SizeBytes is null ? "—" : Formatting.Size(SizeBytes);

    public string DisplayLastPlayed => LastPlayed is { } value
        ? Formatting.RelativeDate(value, DateTimeOffset.Now)
        : "Ещё не запускалась";

    public bool HasCover => CoverPath is not null && File.Exists(CoverPath);

    public bool HasHero => HeroPath is not null && File.Exists(HeroPath);

    /// <summary>Wide art for banners and cards; the cover (cropped) when there is no hero image.</summary>
    public string? BannerPath => HasHero ? HeroPath : CoverPath;

    public override string ToString() => Name;

    public static GameItem FromEntry(GameEntry entry)
    {
        var palette = Palettes[StableHash(entry.Title) % (uint)Palettes.Length];
        return new GameItem
        {
            Entry = entry,
            Name = entry.Title,
            Source = entry.Source,
            InstallPath = entry.InstallPath,
            CoverPath = entry.CoverPath,
            HeroPath = entry.HeroPath,
            SizeBytes = entry.SizeBytes,
            LastPlayed = entry.LastPlayed,
            PaletteFrom = palette.From,
            PaletteTo = palette.To
        };
    }

    /// <summary>FNV-1a: unlike string.GetHashCode it is the same in every run.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return hash;
    }
}

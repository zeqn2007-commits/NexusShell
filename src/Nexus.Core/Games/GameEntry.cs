namespace Nexus.Core.Games;

public enum GameSource
{
    Steam,
    Epic,
    Xbox,
    Gog,
    Local
}

/// <summary>An installed game found by one of the library providers.</summary>
/// <param name="Id">Stable key ("steam:620", "epic:Fortnite", "local:D:\Games\Celeste") used to hide a game.</param>
/// <param name="LaunchTarget">A launcher URI (steam://, com.epicgames.launcher://, shell:AppsFolder\…) or an executable.</param>
/// <param name="CoverPath">Portrait cover art (2:3), when the launcher keeps one locally.</param>
/// <param name="HeroPath">Wide background art for the "continue playing" banner.</param>
public sealed record GameEntry(
    string Id,
    string Title,
    GameSource Source,
    string InstallPath,
    string? LaunchTarget,
    string? CoverPath = null,
    string? HeroPath = null,
    long? SizeBytes = null,
    DateTimeOffset? LastPlayed = null)
{
    public bool IsUri => LaunchTarget is not null
        && (LaunchTarget.Contains("://", StringComparison.Ordinal) || LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
}

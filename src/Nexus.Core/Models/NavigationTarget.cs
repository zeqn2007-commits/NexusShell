namespace Nexus.Core.Models;

public enum NavigationKind
{
    Home,
    Folder,
    OptionalFolder,
    Collection,
    Computer,
    Network,
    Recent,
    Favorites,
    Archive,
    Media,
    Torrents,
    Applications,
    Games,
    AiProjects
}

public sealed record NavigationTarget(
    string Id,
    string Label,
    string Glyph,
    NavigationKind Kind,
    string? Path = null);

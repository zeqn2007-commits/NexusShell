using System.Security;
using Nexus.Core.IO;
using Nexus.Core.Settings;
using Nexus.Core.Shell;

namespace Nexus.Core.Games;

/// <summary>Every installed game: launcher libraries plus the user's own game folders, minus hidden ones.</summary>
public sealed class GameLibrary(SettingsStore settings)
{
    public Task<IReadOnlyList<GameEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(cancellationToken), cancellationToken);

    public IReadOnlyList<GameEntry> Load(CancellationToken cancellationToken = default)
    {
        var current = settings.Current;
        var games = new List<GameEntry>();
        Collect(games, () => SteamLibrary.FindRoot() is { } root ? SteamLibrary.Load(root) : []);
        Collect(games, () => EpicLibrary.Load(EpicLibrary.ManifestsFolder));
        Collect(games, () => XboxLibrary.Load(XboxLibrary.FindRoots()));
        Collect(games, GogLibrary.Load);
        Collect(games, () => LocalGameScanner.Load(current.GameFolders, cancellationToken));
        return Merge(games, current.HiddenGames);
    }

    /// <summary>Hidden games go; a launcher's entry wins over the same folder found by the local scan.</summary>
    public static IReadOnlyList<GameEntry> Merge(IEnumerable<GameEntry> games, IEnumerable<string> hiddenIds)
    {
        var hidden = hiddenIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return games
            .Where(game => !hidden.Contains(game.Id))
            .GroupBy(game => PathHelper.TryNormalize(game.InstallPath) ?? game.InstallPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(game => game.Source == GameSource.Local ? 1 : 0).First())
            .OrderByDescending(game => game.LastPlayed ?? DateTimeOffset.MinValue)
            .ThenBy(game => game.Title, NaturalStringComparer.Instance)
            .ToArray();
    }

    public void Hide(GameEntry game) => settings.Update(current =>
    {
        if (!current.HiddenGames.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
        {
            current.HiddenGames.Add(game.Id);
        }
    });

    public void Unhide(GameEntry game) =>
        settings.Update(current => current.HiddenGames.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase)));

    public int HiddenCount => settings.Current.HiddenGames.Count;

    public void ShowHidden() => settings.Update(current => current.HiddenGames.Clear());

    /// <summary>Adds a folder with games (a library like D:\Games or a single game); false if it is already there.</summary>
    public bool AddFolder(string folder)
    {
        var normalized = PathHelper.Normalize(folder);
        if (settings.Current.GameFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        settings.Update(current => current.GameFolders.Add(normalized));
        return true;
    }

    /// <summary>Starts the game through its launcher (Steam, Epic, Xbox) or its own executable.</summary>
    public static void Launch(GameEntry game)
    {
        if (game.LaunchTarget is not { } target)
        {
            throw new InvalidOperationException($"Не найден способ запустить «{game.Title}».");
        }

        if (game.IsUri)
        {
            ShellLauncher.OpenUri(target);
        }
        else
        {
            ShellLauncher.Launch(target, Path.GetDirectoryName(target));
        }
    }

    private static void Collect(List<GameEntry> games, Func<IReadOnlyList<GameEntry>> provider)
    {
        try
        {
            games.AddRange(provider());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // One broken launcher must not hide the rest of the library.
        }
    }
}

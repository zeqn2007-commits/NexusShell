using Microsoft.Win32;

namespace Nexus.Core.Games;

/// <summary>GOG games registered by their installers under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games.</summary>
public static class GogLibrary
{
    public const string GamesKeyPath = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public static IReadOnlyList<GameEntry> Load()
    {
        using var games = Registry.LocalMachine.OpenSubKey(GamesKeyPath);
        return games is null ? [] : Load(games);
    }

    /// <summary>Reads one game per subkey (gameName, path, exe), as the GOG installer writes them.</summary>
    public static IReadOnlyList<GameEntry> Load(RegistryKey gamesKey)
    {
        var result = new List<GameEntry>();
        foreach (var id in gamesKey.GetSubKeyNames())
        {
            using var game = gamesKey.OpenSubKey(id);
            var title = game?.GetValue("gameName") as string;
            var path = game?.GetValue("path") as string;
            var executable = game?.GetValue("exe") as string;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            result.Add(new GameEntry(
                $"gog:{id}",
                title,
                GameSource.Gog,
                path,
                executable is not null && File.Exists(executable) ? executable : null,
                CoverPath: LocalGameScanner.FindArtwork(path)));
        }

        return result;
    }
}

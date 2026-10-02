using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Nexus.Core.Games;

/// <summary>PC Game Pass / Microsoft Store games installed into XboxGames folders (Content\MicrosoftGame.config).</summary>
public static class XboxLibrary
{
    private const string Base32 = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>XboxGames on every fixed drive, plus folders chosen in the Xbox app (listed in the drive's .GamingRoot).</summary>
    public static IReadOnlyList<string> FindRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                foreach (var folder in ReadGamingRoot(Path.Combine(root, ".GamingRoot")).Append("XboxGames"))
                {
                    var path = Path.Combine(root, folder);
                    if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        roots.Add(path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Drives can go away while they are being listed.
            }
        }

        return roots;
    }

    public static IReadOnlyList<GameEntry> Load(IEnumerable<string> roots)
    {
        var games = new List<GameEntry>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    if (ReadGame(folder) is { } game)
                    {
                        games.Add(game);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skip roots that cannot be listed.
            }
        }

        return games;
    }

    /// <summary>
    /// The package family name Windows derives from the identity: Name_ + 13 characters of the
    /// publisher's SHA-256 (first 64 bits plus a zero bit, in Crockford base32).
    /// </summary>
    public static string PackageFamilyName(string name, string publisher)
    {
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
        var suffix = new char[13];
        for (var index = 0; index < 12; index++)
        {
            suffix[index] = Base32[(int)((bits >> (59 - (index * 5))) & 0x1F)];
        }

        suffix[12] = Base32[(int)((bits & 0xF) << 1)];
        return $"{name}_{new string(suffix)}";
    }

    private static GameEntry? ReadGame(string folder)
    {
        var content = Path.Combine(folder, "Content");
        var configPath = Path.Combine(content, "MicrosoftGame.config");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var game = XDocument.Load(configPath).Root;
            var identity = game?.Element("Identity");
            var name = identity?.Attribute("Name")?.Value;
            var publisher = identity?.Attribute("Publisher")?.Value;
            if (game is null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher))
            {
                return null;
            }

            var visuals = game.Element("ShellVisuals");
            var title = visuals?.Attribute("DefaultDisplayName")?.Value is { Length: > 0 } displayName && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? displayName
                : Path.GetFileName(folder);
            var appId = game.Element("ExecutableList")?.Elements("Executable").Select(executable => executable.Attribute("Id")?.Value)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "Game";
            var splash = visuals?.Attribute("SplashScreenImage")?.Value is { Length: > 0 } image ? Path.Combine(content, image) : null;

            return new GameEntry(
                $"xbox:{name}",
                title,
                GameSource.Xbox,
                folder,
                $@"shell:AppsFolder\{PackageFamilyName(name, publisher)}!{appId}",
                HeroPath: splash is not null && File.Exists(splash) ? splash : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>.GamingRoot: an 8-byte header followed by NUL-separated UTF-16 folder names relative to the drive.</summary>
    private static IEnumerable<string> ReadGamingRoot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length <= 8
                ? []
                : Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(folder => folder.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                    .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

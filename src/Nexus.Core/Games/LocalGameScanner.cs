using System.Diagnostics;

namespace Nexus.Core.Games;

/// <summary>
/// Games in folders the user added (D:\Games, repacks, portable games): every subfolder that
/// holds a plausible game executable becomes a game; a folder that is itself a game counts once.
/// </summary>
public static class LocalGameScanner
{
    private const int MinimumScore = 6;
    private const int MaximumDepth = 3;
    private const int MaximumFolders = 120;
    private const long MinimumExecutableSize = 64 * 1024;

    private static readonly string[] IgnoredFragments =
    [
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "directx",
        "dotnet", "prereq", "crash", "updater", "patcher", "easyanticheat", "benchmark", "dedicatedserver",
        "configtool", "trainer", "keygen", "unlocker", "modmanager", "saveeditor"
    ];

    private static readonly string[] DiscouragedFragments = ["launcher", "editor", "server", "config", "tool"];

    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "Prerequisites", "Installer", "Installers",
        "Support", "CrashReportClient", "DirectX", "vcredist", "__Installer"
    };

    private static readonly string[] EngineMarkers =
    [
        "UnityPlayer.dll", "GameAssembly.dll", "steam_api.dll", "steam_api64.dll", "Galaxy.dll", "Galaxy64.dll",
        "EOSSDK-Win64-Shipping.dll", "data.win"
    ];

    private static readonly string[] ArtworkNames =
    [
        "cover.jpg", "cover.png", "poster.jpg", "poster.png", "folder.jpg", "folder.png", "box.jpg", "box.png", "boxart.jpg", "boxart.png"
    ];

    public static IReadOnlyList<GameEntry> Load(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        var games = new List<GameEntry>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A folder that is itself a game (engine files at its top level) counts once;
            // otherwise it is a library and every subfolder is a candidate.
            if (LooksLikeGame(folder) && FindExecutable(folder, MaximumDepth, cancellationToken) is { } own)
            {
                games.Add(Create(folder, own));
                continue;
            }

            foreach (var candidate in SafeDirectories(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindExecutable(candidate, MaximumDepth, cancellationToken) is { } executable)
                {
                    games.Add(Create(candidate, executable));
                }
            }
        }

        return games;
    }

    /// <summary>Box art the user or a repack left in the game folder (cover.jpg, poster.png…).</summary>
    public static string? FindArtwork(string folder) =>
        ArtworkNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    /// <summary>The executable that most likely starts the game, or null when the folder holds none.</summary>
    public static string? FindExecutable(string folder, int maximumDepth = MaximumDepth, CancellationToken cancellationToken = default)
    {
        var folderName = Normalize(Path.GetFileName(folder.TrimEnd('\\')));
        var best = (Path: (string?)null, Score: int.MinValue, Size: 0L);
        var pending = new Queue<(string Path, int Depth)>([(folder, 0)]);
        var visited = 0;
        while (pending.Count > 0 && visited++ < MaximumFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            foreach (var executable in SafeFiles(current, "*.exe"))
            {
                long size;
                try
                {
                    size = new FileInfo(executable).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var score = Score(folder, folderName, executable, size, depth);
                if (score >= MinimumScore && (score > best.Score || (score == best.Score && size > best.Size)))
                {
                    best = (executable, score, size);
                }
            }

            if (depth < maximumDepth)
            {
                foreach (var child in SafeDirectories(current))
                {
                    pending.Enqueue((child, depth + 1));
                }
            }
        }

        return best.Path;
    }

    private static int Score(string root, string folderName, string executable, long size, int depth)
    {
        var stem = Path.GetFileNameWithoutExtension(executable);
        var normalized = Normalize(stem);
        if (size < MinimumExecutableSize || IgnoredFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            return int.MinValue;
        }

        var score = Math.Max(0, 4 - depth);
        if (normalized.Length >= 3 && folderName.Length >= 3
            && (normalized.Contains(folderName, StringComparison.Ordinal) || folderName.Contains(normalized, StringComparison.Ordinal)))
        {
            score += 8;
        }

        if (HasEngineMarkers(root, executable))
        {
            score += 5;
        }

        if (executable.Contains(@"\Binaries\", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        score += size >= 5L * 1024 * 1024 ? 2 : size >= 1024 * 1024 ? 1 : 0;
        if (DiscouragedFragments.Any(fragment => normalized.EndsWith(fragment, StringComparison.Ordinal)))
        {
            score -= 7;
        }

        return score;
    }

    private static bool HasEngineMarkers(string root, string executable)
    {
        var executableFolder = Path.GetDirectoryName(executable) ?? root;
        var stem = Path.GetFileNameWithoutExtension(executable);
        return new[] { root, executableFolder }.Distinct(StringComparer.OrdinalIgnoreCase).Any(folder =>
            EngineMarkers.Any(marker => File.Exists(Path.Combine(folder, marker)))
            || Directory.Exists(Path.Combine(folder, $"{stem}_Data"))
            || Directory.Exists(Path.Combine(folder, "MonoBleedingEdge"))
            || SafeFiles(folder, "goggame-*.info").Any()
            || SafeFiles(folder, "*.pck").Any()
            || (Directory.Exists(Path.Combine(folder, "Engine")) && Directory.Exists(Path.Combine(folder, "Content"))));
    }

    private static bool LooksLikeGame(string folder) =>
        EngineMarkers.Any(marker => File.Exists(Path.Combine(folder, marker)))
        || SafeFiles(folder, "goggame-*.info").Any()
        || SafeFiles(folder, "*.exe").Any(executable => Directory.Exists(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(executable)}_Data")))
        || (Directory.Exists(Path.Combine(folder, "Engine")) && Directory.Exists(Path.Combine(folder, "Content")));

    private static GameEntry Create(string folder, string executable) => new(
        $"local:{folder}",
        Title(folder, executable),
        GameSource.Local,
        folder,
        executable,
        CoverPath: FindArtwork(folder));

    /// <summary>The product name from the executable when it is meaningful, otherwise the folder name.</summary>
    private static string Title(string folder, string executable)
    {
        var folderName = Path.GetFileName(folder.TrimEnd('\\'));
        try
        {
            var product = FileVersionInfo.GetVersionInfo(executable).ProductName?.Trim();
            return string.IsNullOrWhiteSpace(product) || product.Length < 3
                || product.Contains("Unity", StringComparison.OrdinalIgnoreCase)
                || product.Contains("Unreal", StringComparison.OrdinalIgnoreCase)
                || product.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                    ? folderName
                    : product;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return folderName;
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.GetFiles(folder, pattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(directory => !IgnoredFolders.Contains(directory.Name)
                    && (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .Select(directory => directory.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

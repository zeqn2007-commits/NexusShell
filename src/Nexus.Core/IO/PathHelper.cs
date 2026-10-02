namespace Nexus.Core.IO;

public static class PathHelper
{
    /// <summary>Full path without a trailing separator (except for drive roots like <c>C:\</c>).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool AreEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is strictly inside <paramref name="folder"/>.</summary>
    public static bool IsInside(string candidate, string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && AreEqual(root, path);
    }

    /// <summary>True for <c>\\server</c>: a computer on the network, whose children are its shared folders.</summary>
    public static bool IsNetworkComputer(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 2 && trimmed.StartsWith(@"\\", StringComparison.Ordinal) && trimmed.IndexOf('\\', 2) < 0;
    }

    /// <summary>
    /// The containing folder, or null at the top of a drive or for a network computer.
    /// A share (<c>\\server\share</c>) belongs to its computer (<c>\\server</c>), as in Explorer.
    /// </summary>
    public static string? GetParent(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsNetworkComputer(trimmed))
        {
            return null;
        }

        if (IsDriveRoot(path))
        {
            return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? trimmed[..trimmed.IndexOf('\\', 2)] : null;
        }

        return Path.GetDirectoryName(trimmed);
    }

    /// <summary>"Новая папка", "Новая папка (2)"… — first free name in <paramref name="folder"/>.</summary>
    public static string GetAvailableName(string folder, string baseName, string extension = "")
    {
        var candidate = baseName + extension;
        for (var index = 2; File.Exists(Path.Combine(folder, candidate)) || Directory.Exists(Path.Combine(folder, candidate)); index++)
        {
            candidate = $"{baseName} ({index}){extension}";
        }

        return candidate;
    }
}

using System.IO.Compression;

namespace Nexus.Core.Ai;

public enum AiFileKind
{
    Skill,
    Model,
    McpConfig,
    Prompt
}

/// <summary>An AI-related file or folder found outside the AI tools' own folders.</summary>
public sealed record AiFile(string Path, string Name, AiFileKind Kind, bool IsFolder, long Size, DateTimeOffset Modified)
{
    public string KindTitle => Kind switch
    {
        AiFileKind.Skill => "Скилл",
        AiFileKind.Model => "Модель",
        AiFileKind.McpConfig => "MCP-конфиг",
        _ => "Промпт"
    };
}

/// <summary>
/// The "smart section" of the AI center: skills, models, MCP configs and prompts lying in
/// Downloads, the desktop or Documents, wherever they landed. Nothing is moved; the files are
/// only gathered so they can be found, installed or put away in one click.
/// </summary>
public static class AiFileFinder
{
    private const long MinimumModelSize = 1024 * 1024;
    private const long MaximumJsonSize = 256 * 1024;
    private const long MaximumZipSize = 512L * 1024 * 1024;
    private const int MaximumZipEntries = 4000;

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gguf", ".safetensors", ".ckpt", ".onnx", ".pt", ".pth"
    };

    private static readonly HashSet<string> McpFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcp.json", ".mcp.json", "mcp_config.json", "claude_desktop_config.json"
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "venv", ".venv", "site-packages", "__pycache__", "bin", "obj", "dist", "build", ".cache"
    };

    /// <param name="skipFolders">Folders whose contents are shown elsewhere (known AI projects).</param>
    public static IReadOnlyList<AiFile> Find(IEnumerable<string> roots, IReadOnlySet<string> skipFolders, int maximumDepth = 3, CancellationToken cancellationToken = default)
    {
        var found = new List<AiFile>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Queue<(string Folder, int Depth)>([(root, 0)]);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (folder, depth) = pending.Dequeue();
                foreach (var file in SafeFiles(folder))
                {
                    if (Classify(file) is { } kind)
                    {
                        found.Add(Create(file, kind, isFolder: false));
                    }
                }

                if (depth >= maximumDepth)
                {
                    continue;
                }

                foreach (var child in SafeDirectories(folder))
                {
                    if (skipFolders.Contains(child) || Directory.Exists(Path.Combine(child, ".git")))
                    {
                        continue;
                    }

                    // A downloaded skill is a folder with SKILL.md: it counts as one item.
                    if (File.Exists(Path.Combine(child, SkillLibrary.SkillFileName)))
                    {
                        found.Add(Create(child, AiFileKind.Skill, isFolder: true));
                        continue;
                    }

                    pending.Enqueue((child, depth + 1));
                }
            }
        }

        return found.OrderByDescending(item => item.Modified).ToArray();
    }

    /// <summary>The kind of an AI file by its name and, for archives and JSON, a quick look inside.</summary>
    public static AiFileKind? Classify(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".skill", StringComparison.OrdinalIgnoreCase))
        {
            return AiFileKind.Skill;
        }

        if (name.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".prompty", StringComparison.OrdinalIgnoreCase))
        {
            return AiFileKind.Prompt;
        }

        if (ModelExtensions.Contains(extension))
        {
            return Size(path) >= MinimumModelSize ? AiFileKind.Model : null;
        }

        if (McpFileNames.Contains(name))
        {
            return AiFileKind.McpConfig;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return Size(path) <= MaximumJsonSize && McpConfigs.DeclaresServers(path) ? AiFileKind.McpConfig : null;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Size(path) <= MaximumZipSize && ZipHoldsSkill(path) ? AiFileKind.Skill : null;
        }

        return null;
    }

    /// <summary>True when the archive has a SKILL.md at its root or in its top folder.</summary>
    public static bool ZipHoldsSkill(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Take(MaximumZipEntries).Any(entry =>
                entry.Name.Equals(SkillLibrary.SkillFileName, StringComparison.OrdinalIgnoreCase)
                && entry.FullName.Count(ch => ch is '/' or '\\') <= 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static AiFile Create(string path, AiFileKind kind, bool isFolder)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (isFolder && SkillLibrary.Read(path, string.Empty) is { } skill)
        {
            name = skill.Name;
        }

        DateTimeOffset modified;
        try
        {
            modified = isFolder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            modified = default;
        }

        return new AiFile(path, name, kind, isFolder, isFolder ? 0 : Size(path), modified);
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string[] SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetDirectories()
                .Where(child => !SkippedFolders.Contains(child.Name)
                    && (child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .Select(child => child.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

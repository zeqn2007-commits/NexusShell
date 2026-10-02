using System.Text.Json;

namespace Nexus.Core.Ai;

/// <summary>A folder where AI agents or AI libraries are used.</summary>
/// <param name="Kind">"Проект с агентом" (agent instructions or agent history) or "AI-проект" (AI libraries only).</param>
/// <param name="Signals">Short labels of what was found: "CLAUDE.md", "MCP", "Skills", "AI-код"…</param>
public sealed record AiProject(string Name, string Path, string Kind, DateTimeOffset Modified, IReadOnlyList<string> Signals);

/// <summary>
/// Finds AI projects: folders where Claude Code or Codex ran (their own history), plus folders
/// under the usual project places that carry agent files or AI dependencies.
/// </summary>
public static class AiProjectScanner
{
    public const string AgentProjectKind = "Проект с агентом";
    public const string AiCodeKind = "AI-проект";
    private const int MaximumManifestBytes = 256 * 1024;

    private static readonly (string Name, string Signal, bool IsFolder)[] AgentMarkers =
    [
        ("CLAUDE.md", "CLAUDE.md", false),
        ("AGENTS.md", "AGENTS.md", false),
        ("GEMINI.md", "GEMINI.md", false),
        (".claude", ".claude", true),
        (".cursor", "Cursor", true),
        (".cursorrules", "Cursor", false),
        (".windsurfrules", "Windsurf", false),
        (".mcp.json", "MCP", false),
        (@".claude\skills", "Skills", true),
        (@".github\copilot-instructions.md", "Copilot", false),
        ("Modelfile", "Ollama", false),
        (".aider.conf.yml", "Aider", false)
    ];

    private static readonly string[] DependencyFiles = ["package.json", "requirements.txt", "pyproject.toml", "Pipfile", "environment.yml"];

    private static readonly string[] AiLibraries =
    [
        "openai", "anthropic", "ollama", "transformers", "torch", "tensorflow", "diffusers", "langchain", "llama-index",
        "llama_index", "semantic-kernel", "autogen", "crewai", "modelcontextprotocol", "litellm", "huggingface",
        "sentence-transformers", "onnxruntime", "llama-cpp", "google-genai", "google-generativeai", "@google/generative-ai",
        "mistralai", "groq", "chromadb", "qdrant", "faiss", "vllm", "ultralytics"
    ];

    private static readonly string[] SystemFolders =
    [
        .. new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(folder => folder.Length > 0)
    ];

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "venv", ".venv", "site-packages", "__pycache__", "bin", "obj", "dist", "build", ".cache", "AppData"
    };

    public static IReadOnlyList<AiProject> Load(string userProfile, IEnumerable<string> extraRoots, CancellationToken cancellationToken = default)
    {
        // Folders an agent already worked in carry that history as a signal of their own.
        var history = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ClaudeCodeProjects(userProfile))
        {
            history.TryAdd(folder, "Claude Code");
        }

        foreach (var folder in CodexProjects(userProfile))
        {
            history.TryAdd(folder, "Codex");
        }

        var candidates = new HashSet<string>(history.Keys, StringComparer.OrdinalIgnoreCase);
        var roots = new[] { "Desktop", "Documents", @"source\repos", "Projects", "dev", "src", "code" }
            .Select(folder => Path.Combine(userProfile, folder))
            .Concat(extraRoots)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var folder in SubfoldersUpTo(root, depth: 2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(folder);
            }
        }

        var projects = new List<AiProject>();
        foreach (var folder in candidates.Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signals = AgentMarkers
                .Where(marker => marker.IsFolder ? Directory.Exists(Path.Combine(folder, marker.Name)) : File.Exists(Path.Combine(folder, marker.Name)))
                .Select(marker => marker.Signal)
                .ToList();
            var hasAgent = signals.Count > 0 || history.ContainsKey(folder);
            if (history.TryGetValue(folder, out var agent))
            {
                signals.Insert(0, agent);
            }

            if (UsesAiLibraries(folder))
            {
                signals.Add("AI-код");
            }

            if (signals.Count == 0)
            {
                continue;
            }

            projects.Add(new AiProject(
                Path.GetFileName(folder.TrimEnd('\\')),
                folder,
                hasAgent ? AgentProjectKind : AiCodeKind,
                Directory.GetLastWriteTime(folder),
                signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return projects.OrderByDescending(project => project.Modified).ToArray();
    }

    /// <summary>Folders Claude Code was used in (keys of "projects" in ~/.claude.json), temporary ones left out.</summary>
    public static IReadOnlyList<string> ClaudeCodeProjects(string userProfile)
    {
        var path = Path.Combine(userProfile, ".claude.json");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object
                ? projects.EnumerateObject().Select(project => Normalize(project.Name)).OfType<string>().Where(folder => CanBeProject(folder, userProfile)).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Folders Codex trusts: [projects.'path'] tables in ~/.codex/config.toml.</summary>
    public static IReadOnlyList<string> CodexProjects(string userProfile)
    {
        var path = Path.Combine(userProfile, ".codex", "config.toml");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("[projects.", StringComparison.Ordinal))
                .Select(line => line["[projects.".Length..].TrimEnd(']').Trim().Trim('\'', '"'))
                .Select(Normalize)
                .OfType<string>()
                .Where(folder => CanBeProject(folder, userProfile))
                .Select(TrueCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool UsesAiLibraries(string folder)
    {
        foreach (var name in DependencyFiles)
        {
            var file = Path.Combine(folder, name);
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length <= MaximumManifestBytes)
                {
                    var text = File.ReadAllText(file).ToLowerInvariant();
                    if (AiLibraries.Any(library => text.Contains(library, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable manifests simply do not count.
            }
        }

        return false;
    }

    private static IEnumerable<string> SubfoldersUpTo(string root, int depth)
    {
        var pending = new Queue<(string Folder, int Depth)>([(root, 0)]);
        while (pending.Count > 0)
        {
            var (folder, level) = pending.Dequeue();
            if (level >= depth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = new DirectoryInfo(folder).GetDirectories()
                    .Where(child => !SkippedFolders.Contains(child.Name)
                        && (child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                    .Select(child => child.FullName)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;
                pending.Enqueue((child, level + 1));
            }
        }
    }

    private static string? Normalize(string path) => IO.PathHelper.TryNormalize(path.Replace('/', '\\'));

    /// <summary>
    /// Agents' scratch and temp folders (all under the profile's AppData) come and go, and the places an agent was merely
    /// started in — the profile itself, a drive root, Windows, Program Files — are not projects either.
    /// </summary>
    private static bool CanBeProject(string folder, string userProfile) =>
        !IO.PathHelper.IsInside(folder, Path.Combine(userProfile, "AppData"))
        && !IO.PathHelper.AreEqual(folder, userProfile)
        && !IO.PathHelper.IsDriveRoot(folder)
        && !SystemFolders.Any(system => IO.PathHelper.AreEqual(folder, system) || IO.PathHelper.IsInside(folder, system));

    /// <summary>"c:\users\me\app" as the disk spells it: Codex keeps its paths lowercased.</summary>
    private static string TrueCase(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(path))
            {
                return path;
            }

            var result = root.ToUpperInvariant();
            foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Directory.GetDirectories(result, part).FirstOrDefault() is not { } match)
                {
                    return path;
                }

                result = match;
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}

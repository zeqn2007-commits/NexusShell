using System.Globalization;
using System.Text;
using Nexus.Core.Models;

namespace Nexus.Core.Services;

public sealed class AiWorkspaceService : IAiWorkspaceService
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumFilesPerDirectory = 600;
    private const int MaximumChildrenPerDirectory = 2_000;
    private const int MaximumNestedSourceDirectories = 96;
    private const int MaximumNestedSourceFiles = 256;

    private static readonly HashSet<string> ProjectMarkerNames = new(
        [
            ".git",
            "pyproject.toml",
            "requirements.txt",
            "uv.lock",
            "poetry.lock",
            "Pipfile",
            "environment.yml",
            "environment.yaml",
            "package.json",
            "package-lock.json",
            "pnpm-lock.yaml",
            "yarn.lock",
            "Cargo.toml",
            "go.mod",
            "pom.xml",
            "build.gradle",
            "build.gradle.kts",
            "Dockerfile",
            "compose.yml",
            "compose.yaml",
            "Makefile",
            "CMakeLists.txt",
            "README.md"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> StrongAiMarkerNames = new(
        [
            "Modelfile",
            "mcp.json",
            "mcp.config.json",
            "promptfooconfig.yaml",
            "promptfooconfig.yml",
            "dvc.yaml",
            "agents.md",
            "agent.md",
            "claude.md",
            "gemini.md",
            ".cursorrules",
            ".windsurfrules",
            ".aider.conf.yml",
            ".aider.conf.yaml",
            "comfyui.json",
            "extra_model_paths.yaml",
            "generation_config.json",
            "tokenizer_config.json"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AiDependencyKeywords =
    [
        "openai",
        "anthropic",
        "ollama",
        "transformers",
        "tensorflow",
        "torch",
        "diffusers",
        "huggingface",
        "langchain",
        "llama-index",
        "llama_index",
        "semantic-kernel",
        "semantic_kernel",
        "autogen",
        "crewai",
        "model-context-protocol",
        "\"mcp\"",
        "chromadb",
        "qdrant",
        "faiss",
        "onnxruntime",
        "whisper",
        "stable-diffusion",
        "litellm",
        "gemini",
        "mistral",
        "cohere",
        "groq",
        "deepseek",
        "openrouter",
        "pydantic-ai",
        "pydantic_ai",
        "vllm",
        "azure.ai.openai",
        "microsoft.extensions.ai",
        "microsoft.semantickernel",
        "google-generativeai",
        "google-genai",
        "@google/generative-ai",
        "google.cloud.aiplatform",
        "vertexai",
        "sentence-transformers",
        "sentence_transformers",
        "huggingface-hub",
        "huggingface_hub",
        "llama-cpp-python",
        "llama_cpp",
        "replicate",
        "together-ai",
        "haystack-ai",
        "dspy",
        "mlflow",
        "ultralytics"
    ];

    private static readonly string[] AiNameKeywords =
    [
        " ai",
        "ai-",
        "ai_",
        " llm",
        "llm-",
        "llm_",
        "gpt",
        "ollama",
        "нейро",
        "comfy",
        "diffusion",
        " rag",
        "rag-",
        "rag_",
        " mcp",
        "mcp-",
        "mcp_",
        "codex",
        "claude",
        "anthropic",
        "huggingface",
        "hugging-face",
        "stable diffusion"
    ];

    private static readonly HashSet<string> StrongAiChildDirectoryNames = new(
        [
            "agents",
            "prompts",
            "workflows",
            "mcp",
            "skills",
            ".codex",
            ".agents",
            ".claude",
            ".cursor",
            ".continue"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ModelContainerDirectoryNames = new(
        [
            "models",
            "model",
            "checkpoints",
            "loras",
            "embeddings",
            "tokenizers"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ProjectScopedAiConfigurationNames = new(
        [
            ".claude",
            ".cursor",
            ".continue"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NestedSourceDirectoryNames = new(
        [
            "src",
            "source",
            "sources",
            "app",
            "lib",
            "libs",
            "backend",
            "server",
            "services",
            "api",
            "worker",
            "workers"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        [
            "$Recycle.Bin",
            "System Volume Information",
            "Windows",
            "Program Files",
            "Program Files (x86)",
            "ProgramData",
            "AppData",
            "node_modules",
            ".git",
            ".svn",
            ".hg",
            "bin",
            "obj",
            ".vs",
            ".idea",
            ".venv",
            "venv",
            "__pycache__",
            "packages",
            "cache",
            "caches",
            "Temp",
            "SteamLibrary",
            "Games",
            "Игры",
            ".cache",
            ".gradle",
            ".npm",
            ".nuget",
            ".dotnet",
            ".m2",
            ".local",
            ".vscode",
            ".codex",
            ".agents",
            ".claude",
            ".cursor",
            ".continue",
            ".gemini",
            ".mimocode",
            "site-packages",
            "vendor",
            "dist",
            "build"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ManifestNamesToInspect = new(
        [
            "pyproject.toml",
            "requirements.txt",
            "uv.lock",
            "poetry.lock",
            "Pipfile",
            "environment.yml",
            "environment.yaml",
            "package.json",
            "package-lock.json",
            "pnpm-lock.yaml",
            "yarn.lock",
            "Cargo.toml",
            "go.mod",
            "pom.xml",
            "build.gradle",
            "build.gradle.kts",
            "compose.yml",
            "compose.yaml"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AiSourceExtensions = new(
        [
            ".py",
            ".ipynb",
            ".cs",
            ".fs",
            ".js",
            ".jsx",
            ".ts",
            ".tsx",
            ".java",
            ".kt",
            ".rs",
            ".go"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AiSourcePatterns =
    [
        "import openai",
        "from openai",
        "openai.azure",
        "import anthropic",
        "from anthropic",
        "@anthropic-ai/sdk",
        "import ollama",
        "from ollama",
        "ollama.chat",
        "langchain",
        "llama_index",
        "llama-index",
        "transformers import",
        "from transformers",
        "import diffusers",
        "from diffusers",
        "semantic_kernel",
        "microsoft.semantickernel",
        "import autogen",
        "from autogen",
        "import crewai",
        "from crewai",
        "@modelcontextprotocol/sdk",
        "mcp.server",
        "openaiclient",
        "anthropicclient",
        "using microsoft.extensions.ai",
        "microsoft.semantickernel",
        "azure.ai.openai",
        "google.generativeai",
        "google.genai",
        "from google import genai",
        "import google.generativeai",
        "google.cloud.aiplatform",
        "import vertexai",
        "from vertexai",
        "import torch",
        "from torch",
        "import tensorflow",
        "from tensorflow",
        "import keras",
        "from keras",
        "sentence_transformers",
        "huggingface_hub",
        "llama_cpp",
        "import litellm",
        "from litellm",
        "import dspy",
        "from dspy",
        "import ultralytics",
        "from ultralytics"
    ];

    private static readonly HashSet<string> CommonSourceDirectoryNames = new(
        [
            "src",
            "source",
            "sources",
            "app",
            "apps",
            "lib",
            "libs",
            "tests",
            "test"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ModelExtensions = new(
        [
            ".gguf",
            ".safetensors",
            ".ckpt",
            ".onnx",
            ".pt",
            ".pth",
            ".tflite",
            ".ggml",
            ".bin",
            ".pb",
            ".h5",
            ".keras",
            ".engine"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string> _explicitRoots;
    private readonly bool _includeDefaultRoots;
    private readonly int _maximumDepth;
    private readonly int _maximumVisitedDirectories;
    private readonly int _maximumResults;
    private readonly string? _customRootsFilePath;
    private readonly HashSet<string> _customRoots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly object _rootsGate = new();
    private IReadOnlyList<AiProjectEntry>? _cachedProjects;
    private AiDiscoveryDiagnostics? _lastDiscoveryDiagnostics;
    private DateTimeOffset _cacheCreatedAt;

    public AiWorkspaceService(
        IEnumerable<string>? searchRoots = null,
        bool includeDefaultRoots = true,
        int maximumDepth = 8,
        int maximumVisitedDirectories = 25_000,
        int maximumResults = 500,
        string? customRootsFilePath = null)
    {
        _explicitRoots = (searchRoots ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _includeDefaultRoots = includeDefaultRoots;
        _maximumDepth = Math.Clamp(maximumDepth, 1, 12);
        _maximumVisitedDirectories = Math.Clamp(
            maximumVisitedDirectories,
            100,
            250_000);
        _maximumResults = Math.Clamp(maximumResults, 10, 5_000);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _customRootsFilePath = customRootsFilePath
            ?? (includeDefaultRoots
                ? Path.Combine(
                    localApplicationData,
                    "Nexus Shell",
                    "ai-search-roots.txt")
                : null);
        foreach (var customRoot in LoadCustomRoots(_customRootsFilePath))
        {
            _customRoots.Add(customRoot);
        }

        if (customRootsFilePath is null && includeDefaultRoots)
        {
            var legacyRootsPath = Path.Combine(
                localApplicationData,
                "NexusShell",
                "ai-search-roots.txt");
            foreach (var customRoot in LoadCustomRoots(legacyRootsPath))
            {
                _customRoots.Add(customRoot);
            }
        }
    }

    public IReadOnlyList<string> GetSearchRoots()
    {
        var roots = new HashSet<string>(
            _explicitRoots,
            StringComparer.OrdinalIgnoreCase);
        lock (_rootsGate)
        {
            roots.UnionWith(_customRoots);
        }

        if (_includeDefaultRoots)
        {
            AddDefaultRoots(roots);
            AddConfiguredModelRoots(roots);
        }

        var configuredRoots = Environment.GetEnvironmentVariable(
            "NEXUS_AI_ROOTS");
        if (!string.IsNullOrWhiteSpace(configuredRoots))
        {
            foreach (var configuredRoot in configuredRoots.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries))
            {
                var normalized = TryNormalizePath(
                    Environment.ExpandEnvironmentVariables(configuredRoot));
                if (normalized is not null)
                {
                    roots.Add(normalized);
                }
            }
        }

        return roots
            .Where(Directory.Exists)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetCustomSearchRoots()
    {
        lock (_rootsGate)
        {
            return _customRoots
                .OrderBy(
                    path => path,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public AiDiscoveryDiagnostics? GetLastDiscoveryDiagnostics()
    {
        lock (_cacheGate)
        {
            return _lastDiscoveryDiagnostics;
        }
    }

    public bool AddSearchRoot(string path)
    {
        var normalized = TryNormalizePath(path)
            ?? throw new ArgumentException(
                "Не удалось распознать папку поиска.",
                nameof(path));
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                $"Папка «{normalized}» больше недоступна.");
        }

        lock (_rootsGate)
        {
            if (!_customRoots.Add(normalized))
            {
                return false;
            }

            try
            {
                SaveCustomRoots();
            }
            catch
            {
                _customRoots.Remove(normalized);
                throw;
            }
        }

        InvalidateCache();
        return true;
    }

    public bool RemoveSearchRoot(string path)
    {
        var normalized = TryNormalizePath(path);
        if (normalized is null)
        {
            return false;
        }

        lock (_rootsGate)
        {
            if (!_customRoots.Remove(normalized))
            {
                return false;
            }

            try
            {
                SaveCustomRoots();
            }
            catch
            {
                _customRoots.Add(normalized);
                throw;
            }
        }

        InvalidateCache();
        return true;
    }

    public async Task<IReadOnlyList<AiProjectEntry>> DiscoverProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = GetFreshCache();
        if (cached is not null)
        {
            return cached;
        }

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = GetFreshCache();
            if (cached is not null)
            {
                return cached;
            }

            var discovery = await Task.Run(
                    () => DiscoverProjects(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_cacheGate)
            {
                _cachedProjects = discovery.Projects;
                _lastDiscoveryDiagnostics = discovery.Diagnostics;
                _cacheCreatedAt = DateTimeOffset.UtcNow;
            }

            return discovery.Projects;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedProjects = null;
            _cacheCreatedAt = default;
        }
    }

    private IReadOnlyList<AiProjectEntry>? GetFreshCache()
    {
        lock (_cacheGate)
        {
            return _cachedProjects is not null
                   && DateTimeOffset.UtcNow - _cacheCreatedAt
                   < TimeSpan.FromMinutes(5)
                ? _cachedProjects
                : null;
        }
    }

    private static IReadOnlyList<string> LoadCustomRoots(
        string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath)
            || !File.Exists(settingsPath))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(settingsPath)
                .Select(TryNormalizePath)
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return [];
        }
    }

    private void SaveCustomRoots()
    {
        if (string.IsNullOrWhiteSpace(_customRootsFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_customRootsFilePath)
            ?? throw new IOException(
                "Не удалось определить папку настроек AI-поиска.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".ai-search-roots-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(
                temporaryPath,
                _customRoots.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase));
            File.Move(
                temporaryPath,
                _customRootsFilePath,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
            }
        }
    }

    private DiscoveryRun DiscoverProjects(
        CancellationToken cancellationToken)
    {
        var searchRoots = GetSearchRoots();
        var queue = new Queue<ScanCandidate>();
        foreach (var root in searchRoots)
        {
            queue.Enqueue(new ScanCandidate(root, 0));
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, ScoredProject>(
            StringComparer.OrdinalIgnoreCase);
        var directoryLimitReached = false;
        var depthLimitReached = false;
        var perDirectoryLimitReached = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Count >= _maximumVisitedDirectories)
            {
                directoryLimitReached = true;
                break;
            }

            var candidate = queue.Dequeue();
            var normalizedPath = TryNormalizePath(candidate.Path);
            if (normalizedPath is null
                || !visited.Add(normalizedPath)
                || !Directory.Exists(normalizedPath))
            {
                continue;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(normalizedPath);
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            var children = EnumerateChildDirectories(directory);
            perDirectoryLimitReached |= children.WasTruncated;
            var assessment = AssessDirectory(
                directory,
                children.Items,
                cancellationToken);
            perDirectoryLimitReached |= assessment.WasTruncated;
            if (assessment.Project is not null)
            {
                projects[normalizedPath] = assessment.Project;
            }

            if (candidate.Depth >= _maximumDepth)
            {
                depthLimitReached |= children.WasTruncated
                    || children.Items.Any(child => !ShouldSkipDirectory(child));
                continue;
            }

            foreach (var child in children.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipDirectory(child))
                {
                    continue;
                }

                queue.Enqueue(new ScanCandidate(
                    child.FullName,
                    candidate.Depth + 1));
            }
        }

        var selected = RemoveContainerDuplicates(projects.Values)
            .OrderByDescending(project => project.Entry.ModifiedAt)
            .ThenBy(project => project.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var resultLimitReached = selected.Length > _maximumResults;
        var entries = selected
            .Take(_maximumResults)
            .Select(project => project.Entry)
            .ToArray();
        var diagnostics = new AiDiscoveryDiagnostics(
            searchRoots.Count,
            visited.Count,
            projects.Count,
            entries.Length,
            directoryLimitReached,
            resultLimitReached,
            depthLimitReached,
            perDirectoryLimitReached,
            DateTimeOffset.UtcNow);
        return new DiscoveryRun(entries, diagnostics);
    }

    private static DirectoryAssessment AssessDirectory(
        DirectoryInfo directory,
        IReadOnlyList<DirectoryInfo> children,
        CancellationToken cancellationToken)
    {
        FileInfo[] files;
        var filesWereTruncated = false;
        try
        {
            files = directory
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Take(MaximumFilesPerDirectory + 1)
                .ToArray();
            filesWereTruncated = files.Length > MaximumFilesPerDirectory;
            if (filesWereTruncated)
            {
                files = files.Take(MaximumFilesPerDirectory).ToArray();
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return new DirectoryAssessment(null, false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var score = 0;
        var hasProjectMarker = false;
        var hasExplicitProjectMarker = false;
        var hasAiSignal = false;
        var kind = AiProjectKind.Project;
        var signals = new HashSet<string>(
            StringComparer.CurrentCultureIgnoreCase);
        var childNames = children
            .Select(child => child.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isCodexTask = IsCodexTaskDirectory(directory, childNames);
        if (isCodexTask)
        {
            score += 8;
            hasAiSignal = true;
            hasProjectMarker = true;
            hasExplicitProjectMarker = true;
            kind = AiProjectKind.Project;
            signals.Add("проект Codex");
        }

        var normalizedName = $" {directory.Name.ToLowerInvariant()}";
        var knownWorkspace = ClassifyKnownWorkspace(directory);
        if (knownWorkspace is not null)
        {
            score += 6;
            hasAiSignal = true;
            kind = knownWorkspace.Kind;
            signals.Add(knownWorkspace.Signal);
        }
        else if (AiNameKeywords.Any(normalizedName.Contains))
        {
            score += 3;
            hasAiSignal = true;
            signals.Add("AI в названии");
        }

        foreach (var child in children)
        {
            if (ProjectMarkerNames.Contains(child.Name))
            {
                hasProjectMarker = true;
                hasExplicitProjectMarker = true;
                score++;
            }

            if (StrongAiChildDirectoryNames.Contains(child.Name))
            {
                score += 4;
                hasAiSignal = true;
                signals.Add($"конфигурация {child.Name}");
                if (ProjectScopedAiConfigurationNames.Contains(child.Name))
                {
                    hasProjectMarker = true;
                    hasExplicitProjectMarker = true;
                }

                if (!isCodexTask)
                {
                    kind = AiProjectKind.AgentWorkspace;
                }

                continue;
            }

            if (ModelContainerDirectoryNames.Contains(child.Name))
            {
                var modelArtifacts = ContainsModelArtifact(
                    child,
                    cancellationToken);
                filesWereTruncated |= modelArtifacts.WasTruncated;
                if (modelArtifacts.Found)
                {
                    score += 5;
                    hasAiSignal = true;
                    signals.Add($"модели в {child.Name}");
                    kind = AiProjectKind.ModelWorkspace;
                }
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsProjectMarker(file))
            {
                hasProjectMarker = true;
                hasExplicitProjectMarker = true;
                score++;
            }

            if (StrongAiMarkerNames.Contains(file.Name))
            {
                score += 4;
                hasAiSignal = true;
                hasProjectMarker = true;
                hasExplicitProjectMarker = true;
                signals.Add(file.Name);
                if (!isCodexTask)
                {
                    kind = IsModelConfiguration(file.Name)
                        ? AiProjectKind.ModelWorkspace
                        : AiProjectKind.AgentWorkspace;
                }
            }

            if (ModelExtensions.Contains(file.Extension)
                && IsLikelyModelFile(file))
            {
                score += 5;
                hasAiSignal = true;
                signals.Add(file.Extension);
                kind = AiProjectKind.ModelWorkspace;
            }

            if (ShouldInspectManifest(file)
                && ManifestContainsAiDependency(file, cancellationToken))
            {
                score += 5;
                hasAiSignal = true;
                signals.Add(file.Name);
            }

            if (AiSourceExtensions.Contains(file.Extension)
                && SourceContainsAiUsage(file, cancellationToken))
            {
                score += 5;
                hasAiSignal = true;
                if (!CommonSourceDirectoryNames.Contains(directory.Name))
                {
                    hasProjectMarker = true;
                }

                signals.Add("AI-код");
            }
        }

        if (hasProjectMarker && !hasAiSignal)
        {
            var nestedSource = FindAiUsageInNestedSources(
                children,
                cancellationToken);
            filesWereTruncated |= nestedSource.WasTruncated;
            if (nestedSource.Found)
            {
                score += 5;
                hasAiSignal = true;
                signals.Add("AI-код в исходниках");
            }
        }

        if (!hasAiSignal
            || score < 4
            || !hasProjectMarker
               && kind is (
                   AiProjectKind.Project
                   or AiProjectKind.AgentWorkspace))
        {
            return new DirectoryAssessment(null, filesWereTruncated);
        }

        DateTimeOffset modifiedAt;
        try
        {
            modifiedAt = new DateTimeOffset(
                directory.LastWriteTimeUtc,
                TimeSpan.Zero);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            modifiedAt = DateTimeOffset.MinValue;
        }

        var entry = new AiProjectEntry(
            directory.Name,
            directory.FullName,
            kind,
            modifiedAt,
            signals.OrderBy(
                    signal => signal,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        return new DirectoryAssessment(
            new ScoredProject(
                entry,
                score,
                hasExplicitProjectMarker,
                isCodexTask),
            filesWereTruncated);
    }

    private static bool IsCodexTaskDirectory(
        DirectoryInfo directory,
        IReadOnlySet<string> childNames)
    {
        if (childNames.Contains("work")
            && childNames.Contains("outputs")
            && (childNames.Contains(".codex")
                || childNames.Contains(".agents")))
        {
            return true;
        }

        var dateDirectory = directory.Parent;
        return dateDirectory?.Parent?.Name.Equals(
                   "Codex",
                   StringComparison.OrdinalIgnoreCase) == true
               && DateOnly.TryParseExact(
                   dateDirectory.Name,
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static KnownAiWorkspace? ClassifyKnownWorkspace(
        DirectoryInfo directory)
    {
        var name = directory.Name
            .Trim()
            .TrimStart('.')
            .ToLowerInvariant();
        var compactName = name
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        if (IsConfiguredModelRoot(directory.FullName)
            || compactName is "ollama"
            or "lmstudio"
            or "huggingface"
            or "gpt4all"
            or "jan"
            or "janmodels"
            || name.StartsWith(
                "models--",
                StringComparison.OrdinalIgnoreCase)
            || name.Equals("hub", StringComparison.OrdinalIgnoreCase)
            && directory.Parent?.Name.Equals(
                "huggingface",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return new KnownAiWorkspace(
                AiProjectKind.ModelWorkspace,
                "локальные AI-модели");
        }

        if (compactName.Contains("comfyui", StringComparison.Ordinal)
            || compactName.Contains("automatic1111", StringComparison.Ordinal)
            || compactName.Contains("stablediffusionwebui", StringComparison.Ordinal)
            || compactName.Contains("invokeai", StringComparison.Ordinal)
            || compactName.Contains("textgenerationwebui", StringComparison.Ordinal)
            || compactName.Contains("openwebui", StringComparison.Ordinal)
            || compactName.Contains("fooocus", StringComparison.Ordinal)
            || compactName.Contains("swarmui", StringComparison.Ordinal)
            || compactName.Contains("koboldcpp", StringComparison.Ordinal)
            || compactName.Contains("kohyass", StringComparison.Ordinal)
            || compactName.Contains("stabilitymatrix", StringComparison.Ordinal)
            || compactName is "llamacpp" or "localai")
        {
            return new KnownAiWorkspace(
                AiProjectKind.AiTool,
                "известный AI-инструмент");
        }

        return null;
    }

    private static bool IsConfiguredModelRoot(string path)
    {
        foreach (var variableName in new[]
                 {
                     "OLLAMA_MODELS",
                     "HF_HOME",
                     "HUGGINGFACE_HUB_CACHE",
                     "TRANSFORMERS_CACHE"
                 })
        {
            var configuredPath = Environment.GetEnvironmentVariable(
                variableName);
            var normalizedPath = string.IsNullOrWhiteSpace(configuredPath)
                ? null
                : TryNormalizePath(
                    Environment.ExpandEnvironmentVariables(configuredPath));
            if (normalizedPath is not null
                && PathsEqual(path, normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProjectMarker(FileInfo file)
    {
        if (ProjectMarkerNames.Contains(file.Name))
        {
            return true;
        }

        return file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || file.Name.EndsWith(
                ".code-workspace",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelConfiguration(string fileName) =>
        fileName.Equals("Modelfile", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(
            "generation_config.json",
            StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(
            "tokenizer_config.json",
            StringComparison.OrdinalIgnoreCase);

    private static ArtifactScanResult ContainsModelArtifact(
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
        const int maximumDirectories = 48;
        const int maximumFiles = 192;
        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var directoriesVisited = 0;
        var filesVisited = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directoriesVisited >= maximumDirectories
                || filesVisited >= maximumFiles)
            {
                return new ArtifactScanResult(false, true);
            }

            var candidate = queue.Dequeue();
            directoriesVisited++;
            try
            {
                foreach (var file in candidate.Directory
                             .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                             .Take(maximumFiles - filesVisited + 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    filesVisited++;
                    if (filesVisited > maximumFiles)
                    {
                        return new ArtifactScanResult(false, true);
                    }

                    if (ModelExtensions.Contains(file.Extension)
                        && IsLikelyModelFile(file))
                    {
                        return new ArtifactScanResult(true, false);
                    }
                }

                if (candidate.Depth >= 2)
                {
                    continue;
                }

                foreach (var child in candidate.Directory
                             .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                             .Take(maximumDirectories - directoriesVisited + 1))
                {
                    if (!ShouldSkipArtifactDirectory(child))
                    {
                        queue.Enqueue((child, candidate.Depth + 1));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
            }
        }

        return new ArtifactScanResult(false, false);
    }

    private static bool ShouldSkipArtifactDirectory(DirectoryInfo directory)
    {
        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || directory.Attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static ArtifactScanResult FindAiUsageInNestedSources(
        IReadOnlyList<DirectoryInfo> rootChildren,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        foreach (var child in rootChildren)
        {
            if (NestedSourceDirectoryNames.Contains(child.Name)
                && !ShouldSkipDirectory(child))
            {
                queue.Enqueue((child, 0));
            }
        }

        var directoriesVisited = 0;
        var filesVisited = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directoriesVisited >= MaximumNestedSourceDirectories
                || filesVisited >= MaximumNestedSourceFiles)
            {
                return new ArtifactScanResult(false, true);
            }

            var candidate = queue.Dequeue();
            directoriesVisited++;
            FileInfo[] files;
            DirectoryInfo[] children;
            try
            {
                files = candidate.Directory
                    .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .Take(MaximumNestedSourceFiles - filesVisited + 1)
                    .ToArray();
                children = candidate.Directory
                    .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                    .Take(MaximumNestedSourceDirectories - directoriesVisited + 1)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            if (candidate.Depth > 0
                && (files.Any(IsProjectMarker)
                    || children.Any(child => ProjectMarkerNames.Contains(child.Name))))
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesVisited++;
                if (filesVisited > MaximumNestedSourceFiles)
                {
                    return new ArtifactScanResult(false, true);
                }

                if (AiSourceExtensions.Contains(file.Extension)
                    && SourceContainsAiUsage(file, cancellationToken))
                {
                    return new ArtifactScanResult(true, false);
                }
            }

            if (candidate.Depth >= 3)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!ShouldSkipDirectory(child))
                {
                    queue.Enqueue((child, candidate.Depth + 1));
                }
            }
        }

        return new ArtifactScanResult(false, false);
    }

    private static bool ManifestContainsAiDependency(
        FileInfo manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = ReadSearchableText(
                manifest,
                cancellationToken);
            return AiDependencyKeywords.Any(content.Contains);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool SourceContainsAiUsage(
        FileInfo source,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = ReadSearchableText(source, cancellationToken);
            return AiSourcePatterns.Any(content.Contains);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string ReadSearchableText(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0)
        {
            return string.Empty;
        }

        var length = (int)Math.Min(stream.Length, MaximumManifestBytes);
        var buffer = new byte[length];
        if (stream.Length <= MaximumManifestBytes)
        {
            var bytesRead = ReadFully(
                stream,
                buffer,
                cancellationToken);
            return Encoding.UTF8
                .GetString(buffer, 0, bytesRead)
                .ToLowerInvariant();
        }

        var headLength = length / 2;
        var headRead = ReadFully(
            stream,
            buffer.AsSpan(0, headLength),
            cancellationToken);
        stream.Seek(-1L * (length - headLength), SeekOrigin.End);
        var tailRead = ReadFully(
            stream,
            buffer.AsSpan(headLength),
            cancellationToken);
        return Encoding.UTF8
            .GetString(buffer, 0, headRead + tailRead)
            .ToLowerInvariant();
    }

    private static int ReadFully(
        Stream stream,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static bool ShouldInspectManifest(FileInfo file)
    {
        if (ManifestNamesToInspect.Contains(file.Name))
        {
            return true;
        }

        return file.Extension.Equals(
                ".csproj",
                StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(
                ".fsproj",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyModelFile(FileInfo file)
    {
        try
        {
            if (file.Extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                && !file.Name.Contains("model", StringComparison.OrdinalIgnoreCase)
                && !file.Name.Contains("pytorch", StringComparison.OrdinalIgnoreCase)
                && !file.Name.Contains("consolidated", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var minimumSize = file.Extension.Equals(".pt", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".pth", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".pb", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".h5", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".keras", StringComparison.OrdinalIgnoreCase)
                ? 1024L * 1024L
                : 64L * 1024L;
            return file.Length >= minimumSize;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<ScoredProject> RemoveContainerDuplicates(
        IEnumerable<ScoredProject> projects)
    {
        var discovered = projects.ToList();
        var ordered = discovered
            .Where(candidate =>
                !candidate.Entry.Name.Equals(
                    "Codex",
                    StringComparison.OrdinalIgnoreCase)
                || !discovered.Any(child =>
                    child.IsCodexTask
                    && IsDescendantPath(
                        child.Entry.FullPath,
                        candidate.Entry.FullPath)))
            .OrderByDescending(project => project.Score)
            .ThenByDescending(project => GetPathDepth(project.Entry.FullPath))
            .ToList();
        var selected = new List<ScoredProject>(ordered.Count);

        foreach (var candidate in ordered)
        {
            var overlapping = selected.FirstOrDefault(existing =>
                IsDescendantPath(
                    candidate.Entry.FullPath,
                    existing.Entry.FullPath)
                || IsDescendantPath(
                    existing.Entry.FullPath,
                    candidate.Entry.FullPath));
            if (overlapping is null)
            {
                selected.Add(candidate);
                continue;
            }

            if (PathsEqual(
                    candidate.Entry.FullPath,
                    overlapping.Entry.FullPath))
            {
                continue;
            }

            if (candidate.HasProjectMarker
                && overlapping.HasProjectMarker)
            {
                selected.Add(candidate);
            }
        }

        return selected;
    }

    private static DirectoryEnumeration EnumerateChildDirectories(
        DirectoryInfo directory)
    {
        try
        {
            var children = directory
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Take(MaximumChildrenPerDirectory + 1)
                .ToArray();
            var wasTruncated = children.Length > MaximumChildrenPerDirectory;
            if (wasTruncated)
            {
                children = children
                    .Take(MaximumChildrenPerDirectory)
                    .ToArray();
            }

            return new DirectoryEnumeration(children, wasTruncated);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return new DirectoryEnumeration([], false);
        }
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory)
    {
        if (ExcludedDirectoryNames.Contains(directory.Name))
        {
            return true;
        }

        if (directory.Name.StartsWith(
                ".kimi-",
                StringComparison.OrdinalIgnoreCase)
            || directory.Name.StartsWith(
                ".cache-",
                StringComparison.OrdinalIgnoreCase)
            || directory.Name.EndsWith(
                ".Tests",
                StringComparison.OrdinalIgnoreCase)
            || directory.Name.EndsWith(
                ".Test",
                StringComparison.OrdinalIgnoreCase)
            || directory.Name.EndsWith(
                "-tests",
                StringComparison.OrdinalIgnoreCase)
            || directory.Name.EndsWith(
                "_tests",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || directory.Attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void AddDefaultRoots(ISet<string> roots)
    {
        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        AddIfDirectory(roots, Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments));
        AddIfDirectory(roots, Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory));
        AddIfDirectory(roots, Path.Combine(profile, "Downloads"));
        AddIfDirectory(roots, Path.Combine(profile, ".ollama"));
        AddIfDirectory(roots, Path.Combine(profile, ".lmstudio"));
        AddIfDirectory(
            roots,
            Path.Combine(profile, ".cache", "huggingface"));
        AddIfDirectory(
            roots,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "nomic.ai",
                "GPT4All"));
        AddIfDirectory(
            roots,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Jan"));
        AddIfDirectory(
            roots,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "StabilityMatrix"));
        var candidateNames = new[]
        {
            "Projects",
            "Проекты",
            "Source",
            "Sources",
            "Repos",
            "Repositories",
            "GitHub",
            "Code",
            "Dev",
            "Workspace",
            "Workspaces",
            "AI",
            "ML",
            "LLM",
            "LLMs",
            "ComfyUI",
            "Stable Diffusion",
            "stable-diffusion-webui"
        };

        foreach (var candidateName in candidateNames)
        {
            AddIfDirectory(roots, Path.Combine(profile, candidateName));
        }

        try
        {
            foreach (var profileDirectory in new DirectoryInfo(profile)
                         .EnumerateDirectories()
                         .Take(500))
            {
                if (!ShouldSkipDirectory(profileDirectory))
                {
                    roots.Add(profileDirectory.FullName);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
        }

        var systemRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady
                    || drive.DriveType is not (
                        DriveType.Fixed
                        or DriveType.Removable))
                {
                    continue;
                }

                foreach (var candidateName in candidateNames)
                {
                    AddIfDirectory(
                        roots,
                        Path.Combine(drive.RootDirectory.FullName, candidateName));
                }

                foreach (var topLevel in drive.RootDirectory
                             .EnumerateDirectories()
                             .Take(500))
                {
                    if (string.Equals(
                            drive.RootDirectory.FullName,
                            systemRoot,
                            StringComparison.OrdinalIgnoreCase)
                        && topLevel.Name.Equals(
                            "Users",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!ShouldSkipDirectory(topLevel))
                    {
                        roots.Add(topLevel.FullName);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AddConfiguredModelRoots(ISet<string> roots)
    {
        foreach (var variableName in new[]
                 {
                     "OLLAMA_MODELS",
                     "HF_HOME",
                     "HUGGINGFACE_HUB_CACHE",
                     "TRANSFORMERS_CACHE"
                 })
        {
            var configuredPath = Environment.GetEnvironmentVariable(
                variableName);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                AddIfDirectory(
                    roots,
                    Environment.ExpandEnvironmentVariables(configuredPath));
            }
        }
    }

    private static void AddIfDirectory(ISet<string> roots, string path)
    {
        var normalized = TryNormalizePath(path);
        if (normalized is not null && Directory.Exists(normalized))
        {
            roots.Add(normalized);
        }
    }

    private static int GetPathDepth(string path) =>
        path.Count(character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);

    private static bool IsDescendantPath(string candidate, string parent)
    {
        if (PathsEqual(candidate, parent))
        {
            return false;
        }

        var parentPrefix = parent.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            parentPrefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            right.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? TryNormalizePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(
                    fullPath,
                    root,
                    StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record ScanCandidate(string Path, int Depth);

    private sealed record DirectoryEnumeration(
        IReadOnlyList<DirectoryInfo> Items,
        bool WasTruncated);

    private sealed record DirectoryAssessment(
        ScoredProject? Project,
        bool WasTruncated);

    private sealed record ArtifactScanResult(bool Found, bool WasTruncated);

    private sealed record KnownAiWorkspace(
        AiProjectKind Kind,
        string Signal);

    private sealed record DiscoveryRun(
        IReadOnlyList<AiProjectEntry> Projects,
        AiDiscoveryDiagnostics Diagnostics);

    private sealed record ScoredProject(
        AiProjectEntry Entry,
        int Score,
        bool HasProjectMarker,
        bool IsCodexTask);
}

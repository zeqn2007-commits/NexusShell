using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Core.Ai;

/// <summary>A language model stored on this computer.</summary>
/// <param name="Location">The folder to open for the model.</param>
public sealed record AiModel(
    string Name,
    string Runtime,
    long SizeBytes,
    string? Parameters,
    string? Quantization,
    string Location,
    bool IsLoaded = false);

/// <summary>Models of local runtimes: Ollama (manifests and blobs) and LM Studio (GGUF files).</summary>
public static partial class LocalModels
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Ollama keeps models in %OLLAMA_MODELS% or ~/.ollama/models.</summary>
    public static string OllamaRoot =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } custom ? custom : Path.Combine(UserProfile, ".ollama", "models");

    public static IReadOnlyList<string> LmStudioRoots { get; } =
    [
        Path.Combine(UserProfile, ".lmstudio", "models"),
        Path.Combine(UserProfile, ".cache", "lm-studio", "models")
    ];

    /// <summary>One model per manifest: manifests\{registry}\{namespace}\{model}\{tag}.</summary>
    public static IReadOnlyList<AiModel> LoadOllama(string root)
    {
        var manifests = Path.Combine(root, "manifests");
        if (!Directory.Exists(manifests))
        {
            return [];
        }

        var models = new List<AiModel>();
        foreach (var manifest in SafeFiles(manifests, SearchOption.AllDirectories, "*"))
        {
            var parts = Path.GetRelativePath(manifests, manifest).Split(Path.DirectorySeparatorChar);
            if (parts.Length != 4)
            {
                continue;
            }

            var (registry, space, model, tag) = (parts[0], parts[1], parts[2], parts[3]);
            var name = registry != "registry.ollama.ai" ? $"{registry}/{space}/{model}:{tag}"
                : space == "library" ? $"{model}:{tag}"
                : $"{space}/{model}:{tag}";
            if (ReadOllamaManifest(root, manifest) is { } details)
            {
                models.Add(new AiModel(name, "Ollama", details.Size, details.Parameters, details.Quantization, root));
            }
        }

        return models;
    }

    /// <summary>Names of the models Ollama currently keeps in memory (empty when it is not running).</summary>
    public static async Task<IReadOnlySet<string>> GetLoadedOllamaModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
            using var document = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:11434/api/ps", cancellationToken));
            return document.RootElement.TryGetProperty("models", out var running) && running.ValueKind == JsonValueKind.Array
                ? running.EnumerateArray()
                    .Select(model => model.TryGetProperty("name", out var value) ? value.GetString() : null)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new HashSet<string>();
        }
    }

    /// <summary>LM Studio stores GGUF files as {publisher}\{repository}\{file}.gguf.</summary>
    public static IReadOnlyList<AiModel> LoadLmStudio(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return SafeFiles(root, SearchOption.AllDirectories, "*.gguf")
            .Where(file => !Path.GetFileName(file).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                return new AiModel(
                    stem,
                    "LM Studio",
                    FileSize(file),
                    ParametersPattern().Match(stem) is { Success: true } parameters ? parameters.Groups[1].Value.ToUpperInvariant() + "B" : null,
                    QuantizationPattern().Match(stem) is { Success: true } quantization ? quantization.Value.ToUpperInvariant() : null,
                    Path.GetDirectoryName(file) ?? root);
            })
            .ToArray();
    }

    private static (long Size, string? Parameters, string? Quantization)? ReadOllamaManifest(string root, string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var size = 0L;
            if (manifest.RootElement.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            {
                size += layers.EnumerateArray().Sum(layer => layer.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var value) ? value : 0);
            }

            string? parameters = null;
            string? quantization = null;
            if (manifest.RootElement.TryGetProperty("config", out var config)
                && config.TryGetProperty("digest", out var digest)
                && digest.GetString() is { } blob)
            {
                // The config blob says "model_type": "8.0B", "file_type": "Q4_K_M".
                var blobPath = Path.Combine(root, "blobs", blob.Replace(':', '-'));
                if (File.Exists(blobPath) && FileSize(blobPath) < 64 * 1024)
                {
                    using var details = JsonDocument.Parse(File.ReadAllText(blobPath));
                    parameters = Text(details.RootElement, "model_type");
                    quantization = Text(details.RootElement, "file_type");
                }
            }

            return (size, parameters, quantization);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long FileSize(string path)
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

    private static string[] SafeFiles(string folder, SearchOption option, string pattern)
    {
        try
        {
            return Directory.GetFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 4
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // "Meta-Llama-3-8B-Instruct-Q4_K_M" → 8B, Q4_K_M.
    [GeneratedRegex(@"(?<![A-Za-z0-9.])(\d+(?:\.\d+)?)[Bb](?![A-Za-z0-9])")]
    private static partial Regex ParametersPattern();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:I?Q\d(?:_[0-9A-Z]+)*|BF16|F16|F32)(?![A-Za-z0-9])")]
    private static partial Regex QuantizationPattern();
}

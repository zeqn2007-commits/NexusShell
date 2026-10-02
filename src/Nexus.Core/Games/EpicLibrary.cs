using System.Text.Json;

namespace Nexus.Core.Games;

/// <summary>Installed Epic Games titles from the launcher's install manifests (*.item JSON files).</summary>
public static class EpicLibrary
{
    public static string ManifestsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static IReadOnlyList<GameEntry> Load(string manifestsFolder)
    {
        if (!Directory.Exists(manifestsFolder))
        {
            return [];
        }

        var games = new List<GameEntry>();
        try
        {
            foreach (var manifest in Directory.EnumerateFiles(manifestsFolder, "*.item"))
            {
                if (ReadGame(manifest) is { } game)
                {
                    games.Add(game);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder simply contributes no games.
        }

        return games;
    }

    private static GameEntry? ReadGame(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var title = Text(root, "DisplayName");
            var installPath = Text(root, "InstallLocation");
            var appName = Text(root, "AppName");
            if (title is null || installPath is null || appName is null
                || (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                || !IsGame(root))
            {
                return null;
            }

            var launchId = Text(root, "CatalogNamespace") is { } catalogNamespace && Text(root, "CatalogItemId") is { } catalogItem
                ? $"{catalogNamespace}:{catalogItem}:{appName}"
                : appName;
            long? size = root.TryGetProperty("InstallSize", out var installSize) && installSize.TryGetInt64(out var bytes) && bytes > 0
                ? bytes
                : null;
            return new GameEntry(
                $"epic:{appName}",
                title,
                GameSource.Epic,
                installPath,
                $"com.epicgames.launcher://apps/{Uri.EscapeDataString(launchId)}?action=launch&silent=true",
                SizeBytes: size);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Engines, editors and tools carry no "games" category.</summary>
    private static bool IsGame(JsonElement root) =>
        !root.TryGetProperty("AppCategories", out var categories)
        || categories.ValueKind != JsonValueKind.Array
        || categories.EnumerateArray().Any(category => string.Equals(category.GetString(), "games", StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

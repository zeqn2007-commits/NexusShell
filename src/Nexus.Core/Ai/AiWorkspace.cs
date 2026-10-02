using System.Security;
using Nexus.Core.IO;
using Nexus.Core.Settings;

namespace Nexus.Core.Ai;

/// <summary>Everything the AI center shows, from one scan.</summary>
public sealed record AiSnapshot(
    IReadOnlyList<AiModel> Models,
    IReadOnlyList<AiSkill> Skills,
    IReadOnlyList<McpServer> McpServers,
    IReadOnlyList<AiProject> Projects,
    IReadOnlyList<AiFile> Files)
{
    public static AiSnapshot Empty { get; } = new([], [], [], [], []);
}

/// <summary>Scans local models, skills, MCP servers, AI projects and AI files lying around in the user's folders.</summary>
public sealed class AiWorkspace(SettingsStore settings)
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    private (AiSnapshot Snapshot, DateTimeOffset At)? _latest;

    /// <summary>The last scan, when there was one.</summary>
    public AiSnapshot? Latest => _latest?.Snapshot;

    public DateTimeOffset? LatestAt => _latest?.At;

    /// <summary>The last scan if it is recent (the home page summary), otherwise a new one.</summary>
    public Task<AiSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
        _latest is { } latest && DateTimeOffset.Now - latest.At < FreshFor
            ? Task.FromResult(latest.Snapshot)
            : ScanAsync(cancellationToken);

    public async Task<AiSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        var loadedTask = LocalModels.GetLoadedOllamaModelsAsync(cancellationToken);
        var snapshot = await Task.Run(() => Scan(cancellationToken), cancellationToken).ConfigureAwait(false);
        var loaded = await loadedTask.ConfigureAwait(false);
        if (loaded.Count > 0)
        {
            snapshot = snapshot with
            {
                Models = snapshot.Models
                    .Select(model => model.Runtime == "Ollama" && loaded.Contains(model.Name) ? model with { IsLoaded = true } : model)
                    .ToArray()
            };
        }

        _latest = (snapshot, DateTimeOffset.Now);
        return snapshot;
    }

    /// <summary>The folders searched for AI files: Downloads, the desktop, Documents and the user's extra folders.</summary>
    public IReadOnlyList<string> FileRoots =>
    [
        KnownFolders.GetPath(KnownFolder.Downloads),
        KnownFolders.GetPath(KnownFolder.Desktop),
        KnownFolders.GetPath(KnownFolder.Documents),
        .. settings.Current.AiFolders
    ];

    /// <summary>Adds a folder to search for AI projects and files; false if it is already there.</summary>
    public bool AddFolder(string folder)
    {
        var normalized = PathHelper.Normalize(folder);
        if (settings.Current.AiFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        settings.Update(current => current.AiFolders.Add(normalized));
        return true;
    }

    public void RemoveFolder(string folder) =>
        settings.Update(current => current.AiFolders.RemoveAll(path => PathHelper.AreEqual(path, folder)));

    public IReadOnlyList<string> ExtraFolders => settings.Current.AiFolders;

    private AiSnapshot Scan(CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var models = Safe(() => LocalModels.LoadOllama(LocalModels.OllamaRoot))
            .Concat(LocalModels.LmStudioRoots.SelectMany(root => Safe(() => LocalModels.LoadLmStudio(root))))
            .OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var skills = Safe(() => SkillLibrary.Load(profile));
        var projects = Safe(() => AiProjectScanner.Load(profile, settings.Current.AiFolders, cancellationToken));
        var servers = Safe(() => McpConfigs.Load(profile, appData))
            .Concat(projects.SelectMany(project => Safe(() => McpConfigs.LoadProject(project.Path))))
            .DistinctBy(server => (server.Client, server.Name, server.Command))
            .ToArray();
        var projectFolders = projects.Select(project => project.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Safe(() => AiFileFinder.Find(FileRoots, projectFolders, cancellationToken: cancellationToken));
        return new AiSnapshot(models, skills, servers, projects, files);
    }

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> load)
    {
        try
        {
            return load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // One unreadable source must not hide the rest of the AI center.
            return [];
        }
    }
}

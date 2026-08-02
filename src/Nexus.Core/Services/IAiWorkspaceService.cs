using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IAiWorkspaceService
{
    Task<IReadOnlyList<AiProjectEntry>> DiscoverProjectsAsync(
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetSearchRoots();

    IReadOnlyList<string> GetCustomSearchRoots();

    AiDiscoveryDiagnostics? GetLastDiscoveryDiagnostics();

    bool AddSearchRoot(string path);

    bool RemoveSearchRoot(string path);

    void InvalidateCache();
}

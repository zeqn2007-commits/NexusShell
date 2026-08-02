namespace Nexus.Core.Services;

public interface IFavoritesService
{
    IReadOnlyList<string> GetPaths();

    bool Contains(string path);

    Task<bool> AddAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string path,
        CancellationToken cancellationToken = default);
}

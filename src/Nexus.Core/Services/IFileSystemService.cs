using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IFileSystemService
{
    bool ShowHiddenItems { get; }

    Task SetShowHiddenItemsAsync(
        bool value,
        CancellationToken cancellationToken = default);

    IReadOnlyList<NavigationTarget> GetNavigationTargets();

    Task<IReadOnlyList<FileSystemEntry>> GetDirectoryEntriesAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetPathEntriesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetDrivesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetNetworkLocationsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetRecentFilesAsync(
        IEnumerable<string> roots,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> SearchDirectoryAsync(
        string rootPath,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetTorrentFilesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetTorrentFilesAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileSystemEntry>> GetInstalledApplicationsAsync(
        CancellationToken cancellationToken = default);
}

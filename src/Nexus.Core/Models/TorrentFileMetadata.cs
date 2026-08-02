namespace Nexus.Core.Models;

public sealed record TorrentFileMetadata(
    string? ContentName,
    int? ContentFileCount,
    long? ContentSizeBytes,
    DateTimeOffset? CreatedAt,
    string? CreatedBy,
    string? Comment,
    IReadOnlyList<string> Trackers)
{
    public string ContentSize => ContentSizeBytes is null
        ? string.Empty
        : FileSizeFormatter.Format(ContentSizeBytes.Value);
}

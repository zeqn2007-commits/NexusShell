namespace Nexus.Core.Models;

public sealed record RecycleBinEntry(
    string Id,
    string Name,
    string? OriginalPath,
    DateTimeOffset? DeletedAt,
    long? SizeBytes,
    string DisplayType,
    bool IsDirectory,
    string? RecycledPath);

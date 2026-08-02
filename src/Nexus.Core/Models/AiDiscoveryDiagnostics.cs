namespace Nexus.Core.Models;

public sealed record AiDiscoveryDiagnostics(
    int SearchRootCount,
    int VisitedDirectoryCount,
    int CandidateCount,
    int ResultCount,
    bool DirectoryLimitReached,
    bool ResultLimitReached,
    bool DepthLimitReached,
    bool PerDirectoryLimitReached,
    DateTimeOffset CompletedAt)
{
    public bool IsTruncated =>
        DirectoryLimitReached
        || ResultLimitReached
        || DepthLimitReached
        || PerDirectoryLimitReached;
}

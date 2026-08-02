namespace Nexus.Core.Models;

public sealed record OrganizationSuggestion(
    string SourcePath,
    string DestinationDirectory,
    string DestinationLabel,
    string Reason,
    int ConfidencePercent);

public sealed record OrganizationOperation(
    string SourcePath,
    string DestinationPath,
    DateTimeOffset CompletedAt);

public sealed record OrganizationResult(
    bool Success,
    string Message,
    OrganizationOperation? Operation = null);

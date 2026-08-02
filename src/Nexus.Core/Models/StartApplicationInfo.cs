namespace Nexus.Core.Models;

/// <summary>
/// Describes an application exposed by the Windows Start menu.
/// LaunchTarget is either a real shortcut/file or a shell:AppsFolder target.
/// </summary>
public sealed record StartApplicationInfo(
    string Name,
    string LaunchTarget,
    string? ApplicationId = null,
    string? IconSourcePath = null,
    string? ResolvedExecutablePath = null,
    string? Arguments = null,
    string? WorkingDirectory = null,
    string? SourcePath = null,
    DateTimeOffset? ModifiedAt = null,
    bool IsSystemComponent = false);

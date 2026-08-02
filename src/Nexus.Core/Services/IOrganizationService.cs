using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IOrganizationService
{
    OrganizationSuggestion? Suggest(string filePath);

    Task<OrganizationResult> ApplyAsync(
        OrganizationSuggestion suggestion,
        CancellationToken cancellationToken = default);

    Task<OrganizationResult> UndoAsync(
        OrganizationOperation operation,
        CancellationToken cancellationToken = default);
}

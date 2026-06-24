using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A candidate entity returned from an entity-reference search.
/// </summary>
public sealed record EntityReferenceCandidate(
    string EntityId,
    string DisplayName,
    string Names);

/// <summary>
/// Searches for and resolves referenced entities for the entity-reference field editor. Abstracts
/// the data-access layer's vector query so the editor is unit-testable.
/// </summary>
public interface IEntityReferenceSearch
{
    /// <summary>
    /// Searches for candidate entities matching the supplied text, optionally constrained to the
    /// supplied entity types, using semantic (vector) relevance.
    /// </summary>
    Task<IReadOnlyList<EntityReferenceCandidate>> SearchAsync(
        string searchText,
        IReadOnlyCollection<string> entityTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a single entity by id into a candidate (display name + names) for read-mode display.
    /// </summary>
    Task<EntityReferenceCandidate?> ResolveAsync(
        string entityId,
        CancellationToken cancellationToken = default);
}

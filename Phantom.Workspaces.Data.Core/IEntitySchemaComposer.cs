using System.Text.Json;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Composes and evaluates the schema applicable to an entity's data, returning any validation
/// errors. Shared between the data-access validation pipeline and UI editors so both use one
/// schema-composition implementation and cannot diverge.
/// </summary>
public interface IEntitySchemaComposer
{
    /// <summary>
    /// Validates the supplied entity data against the composed schema for its entity types,
    /// returning a (possibly empty) collection of human-readable validation error messages.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetValidationErrorsAsync(
        JsonElement entityData,
        CancellationToken cancellationToken = default);
}

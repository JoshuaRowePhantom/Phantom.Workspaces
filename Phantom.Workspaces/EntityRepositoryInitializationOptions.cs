using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

internal sealed record EntityRepositoryInitializationOptions
{
    public required RepositorySource RepositorySource { get; init; }

    public string? UserComputerProfileOverride { get; init; }

    public Action<SchemaValidationPass>? ValidationPassStarted { get; init; }
}

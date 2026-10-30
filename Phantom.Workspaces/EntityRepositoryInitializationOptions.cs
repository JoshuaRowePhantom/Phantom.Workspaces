using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces;

internal sealed record EntityRepositoryInitializationOptions
{
    public required RepositorySource RepositorySource { get; init; }

    public IAccessBroker? AccessBroker { get; init; }

    public DevTunnelIdentityRoute? DevTunnelIdentityRoute { get; init; }

    public Func<Phantom.Workspaces.Llm.Interfaces.Access.AccessPresentationScope>? AccessPresentationScope { get; init; }

    public string? UserComputerProfileOverride { get; init; }

    public Action<SchemaValidationPass>? ValidationPassStarted { get; init; }

    // The process fixture prepares and owns the real repository before launching roles.
    // Reopening that repository must not repeat schema installation or identity discovery.
    internal WorkspaceEntitySession? PreparedSession { get; init; }
}

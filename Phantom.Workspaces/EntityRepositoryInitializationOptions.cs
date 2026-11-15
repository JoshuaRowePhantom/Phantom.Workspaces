using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces.Access;
using MongoDB.Driver;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces;

internal sealed record EntityRepositoryInitializationOptions
{
    public required RepositorySource RepositorySource { get; init; }

    public IAccessBroker? AccessBroker { get; init; }

    public DevTunnelIdentityRoute? DevTunnelIdentityRoute { get; init; }

    public Func<Phantom.Workspaces.Llm.Interfaces.Access.AccessPresentationScope>? AccessPresentationScope { get; init; }

    public string? UserComputerProfileOverride { get; init; }

    public Action<SchemaValidationPass>? ValidationPassStarted { get; init; }

    // Supplied clients are borrowed; the provider owns their lifetime.
    internal Func<MongoDbConnectionDefinition, CancellationToken, ValueTask<IMongoClient>>? MongoDbClientFactory { get; init; }

    internal Func<IMongoDatabase, string, MongoDbEntityDataAccessLayer>? MongoDbEntityDataAccessLayerFactory { get; init; }

    // The process fixture prepares and owns the real repository before launching roles.
    // Reopening that repository must not repeat schema installation or identity discovery.
    internal WorkspaceEntitySession? PreparedSession { get; init; }
}

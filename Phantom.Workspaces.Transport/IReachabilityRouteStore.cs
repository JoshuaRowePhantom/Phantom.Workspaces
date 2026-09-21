using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport;

public interface IReachabilityRouteStore
{
    Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
        EntityId profileEntityId,
        CancellationToken cancellationToken = default);

    Task UpsertRouteAsync(
        EntityId profileEntityId,
        ReachabilityRoute route,
        CancellationToken cancellationToken = default);

    Task RemoveRouteAsync(
        EntityId profileEntityId,
        string routeId,
        EntityId ownerProfileEntityId,
        CancellationToken cancellationToken = default);

    Task ClearOwnedRoutesAsync(
        EntityId profileEntityId,
        EntityId ownerProfileEntityId,
        CancellationToken cancellationToken = default);
}

using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport;

public sealed record ReachabilityRoute
{
    public required string RouteId { get; init; }

    public required JsonElement Descriptor { get; init; }

    public required EntityId OwnerProfileEntityId { get; init; }

    public int Priority { get; init; } = 100;

    public required DateTimeOffset LastConfirmed { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

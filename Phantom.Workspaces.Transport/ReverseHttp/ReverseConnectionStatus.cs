namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// Immutable per-client status shape for a registered reverse-HTTP executor client.
/// The transport-layer analogue of the old <c>ConnectedInstanceStatus</c>.
/// </summary>
public sealed record ReverseConnectionStatus
{
    public required string ClientInstanceId { get; init; }

    public DateTimeOffset ConnectedAt { get; init; }

    public int InFlightCount { get; init; }

    /// <summary>
    /// The direct HTTP endpoint the client announced at registration, or null when it registered
    /// only for hub-relayed reverse execution. Consumed by <c>RemoteExecutionRegistry</c> to build a
    /// direct <c>RemoteTrustedExecutor</c> for announced instances.
    /// </summary>
    public string? AnnouncedEndpoint { get; init; }
}

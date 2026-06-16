using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Accepts an inbound reverse-execution duplex channel on the server (S): reads the initial
/// <c>register</c> frame, validates the claimed client instance id, registers a
/// <see cref="ReverseChannelConnection"/> in the <see cref="ReverseExecutionRegistry"/>, and keeps it
/// registered until the channel closes. See <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class ReverseConnectionAcceptor
{
    private readonly ReverseExecutionRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, bool> isKnownClientInstance;

    /// <param name="registry">The registry the accepted connection is registered in.</param>
    /// <param name="isKnownClientInstance">
    /// Validates the claimed client instance id (a user-computer-profile entity id). The server
    /// accepts C's claim within the already-tunnel-authenticated channel; this checks the id is one
    /// the server recognizes. Defaults to accepting any non-empty id.
    /// </param>
    /// <param name="timeProvider">Clock for the connection's connected-at timestamp.</param>
    public ReverseConnectionAcceptor(
        ReverseExecutionRegistry registry,
        Func<string, bool>? isKnownClientInstance = null,
        TimeProvider? timeProvider = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.isKnownClientInstance = isKnownClientInstance ?? (id => !string.IsNullOrWhiteSpace(id));
    }

    /// <summary>
    /// Runs the lifetime of one inbound connection: register, serve, and deregister on close. Returns
    /// when the channel closes or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task AcceptAsync(IReverseMessageChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var registerFrame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (registerFrame is null
            || registerFrame.Type != ReverseFrame.Types.Register
            || string.IsNullOrWhiteSpace(registerFrame.ClientInstanceId)
            || !this.isKnownClientInstance(registerFrame.ClientInstanceId))
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var connection = new ReverseChannelConnection(channel, registerFrame.ClientInstanceId, this.timeProvider.GetUtcNow());
        this.registry.Register(connection);
        connection.Start();
        try
        {
            await using var registration = cancellationToken.Register(() => _ = connection.DisposeAsync());
            await connection.Completion.ConfigureAwait(false);
        }
        finally
        {
            this.registry.Unregister(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

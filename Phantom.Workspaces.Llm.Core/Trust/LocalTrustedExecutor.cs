using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The local (in-process) <see cref="ITrustedExecutor"/>. This is the Llm.Core execution layer
/// responsible for containers, processes, and tool permissions when running on the local client
/// instance (<c>"."</c>).
/// </summary>
/// <remarks>
/// Agent construction is delegated to <see cref="AgentFactory.CreateAgentChatAsync"/>; the trust
/// profile's tool-call policy is enforced via <see cref="TrustToolCallAuthorizer"/>.
/// </remarks>
public sealed class LocalTrustedExecutor : ITrustedExecutor
{
    private readonly Dictionary<string, ILocalStreamHandler> _streamHandlers = new(StringComparer.Ordinal);
    private Func<TrustedToolRequest, CancellationToken, Task>? _toolRunner;

    public LocalTrustedExecutor()
    {
        RegisterStreamHandler("shell", new LocalShellStreamHandler());
    }

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);
        return string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal);
    }

    /// <summary>Creates a tool-call authorizer enforcing the request's trust profile.</summary>
    public static TrustToolCallAuthorizer CreateToolCallAuthorizer(TrustProfile trustProfile)
        => new(trustProfile);

    /// <summary>Registers a handler for a specific stream kind (e.g. <c>"shell"</c>).</summary>
    internal void RegisterStreamHandler(string kind, ILocalStreamHandler handler)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(handler);
        _streamHandlers[kind] = handler;
    }

    /// <summary>
    /// Registers a runner that will be called by <see cref="RunToolAsync"/> when a tool execution
    /// request arrives for the local instance. Only one runner may be registered at a time; calling
    /// this method again replaces the previous registration.
    /// </summary>
    public void RegisterToolRunner(Func<TrustedToolRequest, CancellationToken, Task> runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _toolRunner = runner;
    }

    /// <summary>
    /// Runs the handler for <paramref name="streamKind"/> directly against the supplied
    /// <paramref name="channel"/>, blocking until the handler completes (i.e. the stream lifetime).
    /// This overload lets callers supply their own transport channel (e.g. a WebSocket channel)
    /// without going through the <see cref="InMemoryStreamMessageChannelPair"/> indirection used by
    /// <see cref="OpenStreamAsync"/>.
    /// </summary>
    public Task HandleStreamAsync(
        string streamKind,
        JsonElement openPayload,
        IStreamMessageChannel channel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streamKind);
        ArgumentNullException.ThrowIfNull(channel);

        if (!_streamHandlers.TryGetValue(streamKind, out var handler))
            throw new NotImplementedException(
                $"No local handler is registered for stream kind '{streamKind}'.");

        return handler.HandleAsync(openPayload, channel, ct);
    }

    /// <inheritdoc />
    public Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"LocalTrustedExecutor cannot execute on client instance '{request.TargetClientInstance}'.");
        }

        return AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = request.AgentSessionId,
            AgentServices = request.AgentServices,
        });
    }

    /// <inheritdoc />
    public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_streamHandlers.TryGetValue(request.StreamKind, out var handler))
            throw new NotImplementedException(
                $"No local handler is registered for stream kind '{request.StreamKind}'.");

        var pair = new InMemoryStreamMessageChannelPair();
        _ = handler.HandleAsync(request.OpenPayload, pair.HostEnd, ct);
        return Task.FromResult<Stream>(new StreamMessageChannelStream(pair.ClientEnd));
    }

    /// <inheritdoc />
    public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"LocalTrustedExecutor cannot run a tool on client instance '{request.TargetClientInstance}'.");
        }

        if (_toolRunner is null)
        {
            throw new NotSupportedException("No tool runner has been registered on this LocalTrustedExecutor.");
        }

        return _toolRunner(request, cancellationToken);
    }
}

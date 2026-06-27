using System.IO;
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
}

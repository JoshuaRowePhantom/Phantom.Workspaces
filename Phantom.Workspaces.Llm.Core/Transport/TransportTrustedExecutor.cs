using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Mcp;
using Phantom.Workspaces.Transport.Shell;

namespace Phantom.Workspaces.Llm.Core.Transport;

/// <summary>
/// The transport-backed <see cref="ITrustedExecutor"/>. Produces agent chats / streams / tool runs
/// by resolving a connection descriptor via <see cref="ExecutionTargetResolver"/> and connecting
/// through an <see cref="ITransportFactoryRegistry"/>, then wrapping the resulting
/// <see cref="ITransport"/> in <see cref="ChatClientOverTransport"/> / <see cref="ShellOverTransport"/> /
/// <see cref="McpClientOverTransport"/>. Transport-layer replacement for <c>ReverseTrustedExecutor</c>.
/// </summary>
public sealed class TransportTrustedExecutor : ITrustedExecutor, IAsyncDisposable
{
    private readonly ITransportFactoryRegistry transportFactoryRegistry;
    private readonly ExecutionTargetResolver executionTargetResolver;
    private readonly ConcurrentBag<ITransport> transports = new();
    private int disposed;

    public TransportTrustedExecutor(
        ITransportFactoryRegistry transportFactoryRegistry,
        ExecutionTargetResolver executionTargetResolver)
    {
        this.transportFactoryRegistry = transportFactoryRegistry ?? throw new ArgumentNullException(nameof(transportFactoryRegistry));
        this.executionTargetResolver = executionTargetResolver ?? throw new ArgumentNullException(nameof(executionTargetResolver));
    }

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);
        return this.executionTargetResolver.CanResolve(targetClientInstance);
    }

    /// <inheritdoc />
    public async Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transport = await this.ConnectAsync(request.TargetClientInstance, cancellationToken).ConfigureAwait(false);
        var chatClient = new ChatClientOverTransport(transport, BuildChatClientRequest(request));

        var baseServices = request.AgentServices ?? new AgentServices();
        var services = request.PreserveSourcePersistence
            ? baseServices with { ChatClientOverride = chatClient }
            : baseServices with
            {
                ChatClientOverride = chatClient,
                AgentPersistenceStoreOverride = NullAgentPersistenceStore.Instance,
            };

        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = request.AgentSessionId,
            AgentServices = services,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transport = await this.ConnectAsync(request.TargetClientInstance, ct).ConfigureAwait(false);
        var shell = new ShellOverTransport(transport, BuildStreamRequest(request));
        await shell.OpenAsync(ct).ConfigureAwait(false);
        return shell.Stream;
    }

    /// <inheritdoc />
    public async Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transport = await this.ConnectAsync(request.TargetClientInstance, cancellationToken).ConfigureAwait(false);
        await using var mcpClient = new McpClientOverTransport(transport, BuildToolConnectionRequest(request));

        await mcpClient.SendAsync(BuildRunToolMessage(request), cancellationToken).ConfigureAwait(false);
        var response = await mcpClient.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "tool-error", StringComparison.OrdinalIgnoreCase))
        {
            var message = response.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            throw new TransportException(
                message ?? $"Tool '{request.ToolTypeName}' failed on client instance '{request.TargetClientInstance}'.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        while (this.transports.TryTake(out var transport))
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ITransport> ConnectAsync(string targetClientInstance, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        var descriptor = this.executionTargetResolver.ResolveDescriptor(targetClientInstance);
        var transport = await this.transportFactoryRegistry.ConnectToAsync(descriptor, ct).ConfigureAwait(false);
        this.transports.Add(transport);
        return transport;
    }

    private static JsonElement BuildChatClientRequest(TrustedExecutionRequest request)
    {
        var descriptor = new JsonObject
        {
            ["type"] = "chat-client",
            ["agent-definition"] = request.AgentDefinition.ToJson(),
        };

        if (request.AgentSessionId is { } sessionId)
        {
            descriptor["agent-session-id"] = sessionId;
        }

        return JsonSerializer.SerializeToElement(descriptor);
    }

    private static JsonElement BuildStreamRequest(TrustedStreamRequest request)
    {
        var descriptor = new JsonObject { ["type"] = request.StreamKind };

        if (request.OpenPayload.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in request.OpenPayload.EnumerateObject())
            {
                descriptor[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return JsonSerializer.SerializeToElement(descriptor);
    }

    private static JsonElement BuildToolConnectionRequest(TrustedToolRequest request)
    {
        var descriptor = new JsonObject
        {
            ["type"] = "mcp",
            ["connection"] = new JsonObject
            {
                ["tool-type-name"] = request.ToolTypeName,
                ["tool-entity-id"] = request.ToolEntityId,
            },
            ["target-client-instance"] = request.TargetClientInstance,
        };

        return JsonSerializer.SerializeToElement(descriptor);
    }

    private static JsonElement BuildRunToolMessage(TrustedToolRequest request)
    {
        var message = new JsonObject
        {
            ["type"] = "run-tool",
            ["tool-type-name"] = request.ToolTypeName,
            ["tool-entity-id"] = request.ToolEntityId,
        };

        return JsonSerializer.SerializeToElement(message);
    }
}

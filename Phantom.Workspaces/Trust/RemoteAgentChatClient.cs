using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// An <see cref="IChatClient"/> that relays a single agent turn to the remote
/// <c>POST /agent/chat/{sessionId}/turn</c> endpoint, which owns a cached <see cref="AgentChat"/>
/// keyed by <see cref="AgentSessionId"/>. Unlike <see cref="WebRemoteChatClient"/>, this client:
/// <list type="bullet">
/// <item>Sends only the <em>latest</em> user message(s), not the full conversation history.</item>
/// <item>Receives a true streaming NDJSON response rather than waiting for a batch reply.</item>
/// </list>
/// </summary>
/// <remarks>
/// Use this client for stateful providers (e.g. <c>CopilotSdkChatClient</c>) where the remote
/// host must maintain a persistent session across turns. For stateless providers the existing
/// <see cref="WebRemoteChatClient"/> also works, but routing through this client is equally valid.
/// </remarks>
public sealed class RemoteAgentChatClient : IChatClient
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string agentDefinitionJson;
    private readonly string agentSessionId;

    /// <summary>Creates a remote agent-chat client targeting the given host endpoint.</summary>
    /// <param name="endpoint">Absolute base URL of the remote Phantom.Workspaces host.</param>
    /// <param name="agentDefinitionJson">The agent definition JSON to execute remotely.</param>
    /// <param name="agentSessionId">The session id shared between the local and remote <c>AgentChat</c>.</param>
    /// <param name="devTunnelAccessToken">Optional dev tunnel access token.</param>
    /// <param name="httpClient">Optional injected HTTP client (testing).</param>
    public RemoteAgentChatClient(
        string endpoint,
        string agentDefinitionJson,
        string agentSessionId,
        string? devTunnelAccessToken = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDefinitionJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSessionId);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"Remote host endpoint is not a valid absolute URI: {endpoint}");
        }

        this.httpClient = httpClient ?? new HttpClient { BaseAddress = endpointUri };
        this.ownsHttpClient = httpClient is null;
        this.httpClient.BaseAddress ??= endpointUri;
        this.agentDefinitionJson = agentDefinitionJson;
        this.agentSessionId = agentSessionId;

        if (!string.IsNullOrWhiteSpace(devTunnelAccessToken)
            && !this.httpClient.DefaultRequestHeaders.Contains("X-Tunnel-Authorization"))
        {
            this.httpClient.DefaultRequestHeaders.Add("X-Tunnel-Authorization", $"tunnel {devTunnelAccessToken}");
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = new AgentChatTurnRequest
        {
            AgentDefinitionJson = this.agentDefinitionJson,
            AgentSessionId = this.agentSessionId,
            Messages = [.. messages],
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/agent/chat/{Uri.EscapeDataString(this.agentSessionId)}/turn");
        httpRequest.Content = JsonContent.Create(request, options: SerializerOptions);

        using var response = await this.httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Remote agent host returned {(int)response.StatusCode}: {body}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var update = JsonSerializer.Deserialize<ChatResponseUpdate>(line, SerializerOptions);
            if (update is not null)
            {
                yield return update;
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.ownsHttpClient)
        {
            this.httpClient.Dispose();
        }
    }
}

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
/// An <see cref="IChatClient"/> that relays a conversation to a remote Phantom.Workspaces host,
/// which runs the agent (model and tools) under its trust profile and returns the response.
/// </summary>
/// <remarks>
/// This is the client side of the Workspaces remoting layer. The local agent shell is thin: it
/// forwards messages to the remote host and surfaces the response, while the remote host performs
/// the trusted execution (containers, processes, tool permissions).
/// </remarks>
public sealed class WebRemoteChatClient : IChatClient
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string agentDefinitionJson;
    private readonly string? agentSessionId;

    /// <summary>Creates a remote chat client targeting the given host endpoint.</summary>
    /// <param name="endpoint">Absolute base URL of the remote Phantom.Workspaces host.</param>
    /// <param name="agentDefinitionJson">The agent definition JSON to execute remotely.</param>
    /// <param name="agentSessionId">Optional remote agent session id.</param>
    /// <param name="devTunnelAccessToken">Optional dev tunnel access token for non-interactive access.</param>
    /// <param name="httpClient">Optional injected HTTP client (testing); otherwise one is created and owned.</param>
    public WebRemoteChatClient(
        string endpoint,
        string agentDefinitionJson,
        string? agentSessionId = null,
        string? devTunnelAccessToken = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDefinitionJson);
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
        ArgumentNullException.ThrowIfNull(messages);

        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = this.agentDefinitionJson,
            AgentSessionId = this.agentSessionId,
            Messages = [.. messages],
        };

        using var response = await this.httpClient
            .PostAsJsonAsync("/agent/respond", request, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Remote agent host returned {(int)response.StatusCode}: {body}");
        }

        var chatResponse = await response.Content
            .ReadFromJsonAsync<ChatResponse>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return chatResponse
            ?? throw new InvalidOperationException("Remote agent host returned an empty response.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await this.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
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

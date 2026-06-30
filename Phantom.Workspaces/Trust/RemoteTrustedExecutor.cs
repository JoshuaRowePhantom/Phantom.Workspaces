using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// The Workspaces remoting <see cref="ITrustedExecutor"/>. It runs an agent on a remote
/// Phantom.Workspaces host (identified by a client instance id) by building a thin local agent
/// shell whose chat client relays the conversation to the remote host over HTTP.
/// </summary>
/// <remarks>
/// This is the application-layer counterpart to the Llm.Core <c>LocalTrustedExecutor</c>: trusted
/// execution (containers, processes, tool permissions) happens on the remote host, while this
/// executor owns the cross-machine transport.
/// </remarks>
public sealed class RemoteTrustedExecutor : ITrustedExecutor
{
    private readonly string clientInstance;
    private readonly string endpoint;
    private readonly string? devTunnelAccessToken;
    private readonly HttpMessageHandler? httpMessageHandler;

    /// <summary>
    /// Creates a remote executor for a specific client instance and its host endpoint.
    /// </summary>
    /// <param name="clientInstance">The remote client instance id this executor serves.</param>
    /// <param name="endpoint">Absolute base URL of the remote Phantom.Workspaces host.</param>
    /// <param name="devTunnelAccessToken">Optional dev tunnel access token for non-interactive access.</param>
    /// <param name="httpMessageHandler">Optional HTTP message handler (for testing).</param>
    public RemoteTrustedExecutor(
        string clientInstance,
        string endpoint,
        string? devTunnelAccessToken = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (string.Equals(clientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "RemoteTrustedExecutor cannot serve the local client instance '.'.",
                nameof(clientInstance));
        }

        this.clientInstance = clientInstance;
        this.endpoint = endpoint;
        this.devTunnelAccessToken = devTunnelAccessToken;
        this.httpMessageHandler = httpMessageHandler;
    }

    /// <summary>The remote client instance id this executor serves.</summary>
    public string ClientInstance => this.clientInstance;

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);
        return string.Equals(targetClientInstance, this.clientInstance, StringComparison.Ordinal);
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
                $"RemoteTrustedExecutor for '{this.clientInstance}' cannot execute on client instance "
                + $"'{request.TargetClientInstance}'.");
        }

        var sessionId = request.AgentSessionId ?? Guid.NewGuid().ToString("n");

        HttpClient? testHttpClient = this.httpMessageHandler is not null
            ? new HttpClient(this.httpMessageHandler) { BaseAddress = new Uri(this.endpoint) }
            : null;

        var remoteChatClient = new RemoteAgentChatClient(
            this.endpoint,
            request.AgentDefinition.ToJson(),
            sessionId,
            this.devTunnelAccessToken,
            testHttpClient);

        var baseServices = request.AgentServices ?? new AgentServices();
        var services = baseServices with
        {
            ChatClientOverride = remoteChatClient,
            AgentPersistenceStoreOverride = NullAgentPersistenceStore.Instance,
        };

        return AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = sessionId,
            AgentServices = services,
        });
    }

    /// <inheritdoc />
    public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"RemoteTrustedExecutor for '{this.clientInstance}' cannot open a stream on client instance "
                + $"'{request.TargetClientInstance}'.");
        }

        var client = new WebRemoteStreamClient(this.endpoint, this.devTunnelAccessToken);
        return client.OpenAsync(request, ct);
    }

    /// <inheritdoc />
    public async Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"RemoteTrustedExecutor for '{this.clientInstance}' cannot run a tool on client instance "
                + $"'{request.TargetClientInstance}'.");
        }

        using var httpClient = CreateHttpClient(this.endpoint, this.devTunnelAccessToken);
        using var response = await httpClient
            .PostAsJsonAsync("/workspace/tools/run", request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Remote tool host returned {(int)response.StatusCode}: {body}");
        }
    }

    private static HttpClient CreateHttpClient(string endpoint, string? devTunnelAccessToken)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
        if (!string.IsNullOrWhiteSpace(devTunnelAccessToken))
        {
            httpClient.DefaultRequestHeaders.Add("X-Tunnel-Authorization", $"tunnel {devTunnelAccessToken}");
        }

        return httpClient;
    }
}

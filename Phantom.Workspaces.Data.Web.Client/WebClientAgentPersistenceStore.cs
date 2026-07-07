using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.Web.Client;

public sealed class WebClientAgentPersistenceStore : IAgentPersistenceStore, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly Func<string?>? devTunnelAccessTokenResolver;
    private static readonly JsonSerializerOptions JsonSerializerOptions = AIJsonUtilities.DefaultOptions;

    public WebClientAgentPersistenceStore(
        string endpoint,
        string? devTunnelAccessToken = null,
        Func<string?>? devTunnelAccessTokenResolver = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"Web agent persistence endpoint is not a valid absolute URI: {endpoint}");
        }

        this.httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = endpointUri,
        };
        this.ownsHttpClient = httpClient is null;
        this.devTunnelAccessTokenResolver = devTunnelAccessTokenResolver;

        if (this.httpClient.BaseAddress is null)
        {
            this.httpClient.BaseAddress = endpointUri;
        }

        if (!string.IsNullOrWhiteSpace(devTunnelAccessToken)
            && !this.httpClient.DefaultRequestHeaders.Contains("X-Tunnel-Authorization"))
        {
            this.httpClient.DefaultRequestHeaders.Add("X-Tunnel-Authorization", $"tunnel {devTunnelAccessToken}");
        }
    }

    public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        var dto = new StoreAgentRequest
        {
            Agent = ToDto(request.Agent),
            NewMessages = request.NewMessages,
        };

        await this.PostAsync<StoreAgentRequest, object>(
            "/agent/persistence/store",
            dto,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await this.httpClient.PostAsJsonAsync(
                "/agent/persistence/restore",
                new { AgentSessionId = request.AgentSessionId },
                JsonSerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new WebDataAccessRequestException(
                $"Web agent persistence call to '/agent/persistence/restore' could not reach the server: {exception.Message}",
                exception.StatusCode,
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDataAccessRequestException(
                "Web agent persistence call to '/agent/persistence/restore' timed out.",
                statusCode: null,
                exception);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && this.devTunnelAccessTokenResolver is not null)
        {
            response.Dispose();
            var freshToken = this.devTunnelAccessTokenResolver();
            if (!string.IsNullOrWhiteSpace(freshToken))
            {
                this.httpClient.DefaultRequestHeaders.Remove("X-Tunnel-Authorization");
                this.httpClient.DefaultRequestHeaders.Add("X-Tunnel-Authorization", $"tunnel {freshToken}");
            }

            try
            {
                response = await this.httpClient.PostAsJsonAsync(
                    "/agent/persistence/restore",
                    new { AgentSessionId = request.AgentSessionId },
                    JsonSerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new WebDataAccessRequestException(
                    $"Web agent persistence call to '/agent/persistence/restore' could not reach the server: {exception.Message}",
                    exception.StatusCode,
                    exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebDataAccessRequestException(
                    "Web agent persistence call to '/agent/persistence/restore' timed out.",
                    statusCode: null,
                    exception);
            }
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new WebDataAccessRequestException(
                    $"Web agent persistence call to '/agent/persistence/restore' failed with {(int)response.StatusCode}: {errorBody}",
                    response.StatusCode);
            }

            var dto = await response.Content.ReadFromJsonAsync<PersistedAgentDto>(JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            if (dto is null)
            {
                throw new WebDataAccessRequestException(
                    "Web agent persistence endpoint '/agent/persistence/restore' returned an empty response.",
                    response.StatusCode);
            }

            return FromDto(dto);
        }
    }

    public async ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await this.PostAsync<object, ReadMessagesResponse>(
            "/agent/persistence/messages",
            new { AgentSessionId = request.AgentSessionId },
            cancellationToken).ConfigureAwait(false);

        return response.Messages;
    }

    public async ValueTask AddSubAgentLinkAsync(
        string parentSessionId,
        string childSessionId,
        CancellationToken cancellationToken = default)
    {
        var dto = new AddSubAgentLinkRequest
        {
            ParentSessionId = parentSessionId,
            ChildSessionId = childSessionId,
        };

        await this.PostAsync<AddSubAgentLinkRequest, object>(
            "/agent/persistence/sub-agent-links/add",
            dto,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
    {
        var response = await this.PostAsync<object, ReadSubAgentChildIdsResponse>(
            "/agent/persistence/sub-agent-links/read",
            new { ParentSessionId = parentSessionId },
            cancellationToken).ConfigureAwait(false);

        return response.ChildSessionIds.Select(static id => new AgentSessionId(id)).ToList();
    }

    public void Dispose()
    {
        if (this.ownsHttpClient)
        {
            this.httpClient.Dispose();
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativeUri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await this.httpClient.PostAsJsonAsync(relativeUri, request, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new WebDataAccessRequestException(
                $"Web agent persistence call to '{relativeUri}' could not reach the server: {exception.Message}",
                exception.StatusCode,
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDataAccessRequestException(
                $"Web agent persistence call to '{relativeUri}' timed out.",
                statusCode: null,
                exception);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && this.devTunnelAccessTokenResolver is not null)
        {
            response.Dispose();
            var freshToken = this.devTunnelAccessTokenResolver();
            if (!string.IsNullOrWhiteSpace(freshToken))
            {
                this.httpClient.DefaultRequestHeaders.Remove("X-Tunnel-Authorization");
                this.httpClient.DefaultRequestHeaders.Add("X-Tunnel-Authorization", $"tunnel {freshToken}");
            }

            try
            {
                response = await this.httpClient.PostAsJsonAsync(relativeUri, request, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new WebDataAccessRequestException(
                    $"Web agent persistence call to '{relativeUri}' could not reach the server: {exception.Message}",
                    exception.StatusCode,
                    exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebDataAccessRequestException(
                    $"Web agent persistence call to '{relativeUri}' timed out.",
                    statusCode: null,
                    exception);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new WebDataAccessRequestException(
                    $"Web agent persistence call to '{relativeUri}' failed with {(int)response.StatusCode}: {errorBody}",
                    response.StatusCode);
            }

            if (typeof(TResponse) == typeof(object))
            {
                return default!;
            }

            var responseBody = await response.Content.ReadFromJsonAsync<TResponse>(JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            return responseBody ?? throw new WebDataAccessRequestException(
                $"Web agent persistence endpoint '{relativeUri}' returned an empty response.",
                response.StatusCode);
        }
    }

    private static PersistedAgentDto ToDto(PersistedAgent agent) => new()
    {
        AgentSessionId = agent.AgentSessionId,
        AgentSessionJson = agent.AgentSessionJson.ToJsonElement(),
        AgentDefinitionJson = agent.AgentDefinitionJson.ToJsonElement(),
        CopilotSdkSessionId = agent.CopilotSdkSessionId,
    };

    private static PersistedAgent FromDto(PersistedAgentDto dto) => new()
    {
        AgentSessionId = dto.AgentSessionId,
        AgentSessionJson = dto.AgentSessionJson.ToBsonDocument(),
        AgentDefinitionJson = dto.AgentDefinitionJson.ToBsonDocument(),
        CopilotSdkSessionId = dto.CopilotSdkSessionId,
    };
}

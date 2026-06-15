using System.Net.Http.Json;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Web.Client;

public sealed class WebClientDataAccessLayer : IDataAccessLayer, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private static readonly JsonSerializerOptions JsonSerializerOptions = WebDataAccessJsonSerialization.Options;

    public WebClientDataAccessLayer(
        string endpoint,
        string? devTunnelAccessToken = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"Web data access endpoint is not a valid absolute URI: {endpoint}");
        }

        this.httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = endpointUri,
        };
        this.ownsHttpClient = httpClient is null;

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

    public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        => this.PostAsync<UpdateRequest, UpdateResult>("/data/update", request, cancellationToken);

    public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        => this.PostAsync<GetRequest, GetResult>("/data/get", request, cancellationToken);

    public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        => this.PostAsync<QueryRequest, QueryResult>("/data/query", request, cancellationToken);

    public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        => this.PostAsync<GetHistoryRequest, GetHistoryResult>("/data/get-history", request, cancellationToken);

#pragma warning disable CS0618
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        => this.PostAsync<ExportRequest, ExportResult>("/data/export", request, cancellationToken);
#pragma warning restore CS0618

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
        => this.PostAsync<GetChangedEntitiesRequest, GetChangedEntitiesResult>("/data/get-changed-entities", request, cancellationToken);

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
        using var response = await this.httpClient.PostAsJsonAsync(relativeUri, request, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Web data access call to '{relativeUri}' failed with {(int)response.StatusCode}: {errorBody}");
        }

        var responseBody = await response.Content.ReadFromJsonAsync<TResponse>(JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        return responseBody ?? throw new InvalidOperationException($"Web data access endpoint '{relativeUri}' returned an empty response.");
    }
}

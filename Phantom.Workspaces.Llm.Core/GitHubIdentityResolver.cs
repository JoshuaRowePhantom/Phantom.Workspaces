using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves GitHub usernames from GitHub tokens by calling <c>GET https://api.github.com/user</c>.
/// Results are cached in-process by token so repeated auth events for the same token do not
/// incur additional HTTP round-trips.
/// </summary>
public sealed class GitHubIdentityResolver : IGitHubIdentityResolver
{
    private const string GitHubUserEndpoint = "https://api.github.com/user";

    private readonly ConcurrentDictionary<string, Task<string?>> cache = new();
    private readonly IHttpClientFactory? httpClientFactory;
    private readonly ILogger<GitHubIdentityResolver> logger;

    public GitHubIdentityResolver(
        IHttpClientFactory? httpClientFactory = null,
        ILogger<GitHubIdentityResolver>? logger = null)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger ?? NullLogger<GitHubIdentityResolver>.Instance;
    }

    /// <inheritdoc />
    public Task<string?> GetUsernameAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // GetOrAdd returns the existing task when a concurrent call is already in flight for the
        // same token, so each token produces at most one HTTP request regardless of concurrency.
        return this.cache.GetOrAdd(token, this.ResolveAsync);
    }

    private async Task<string?> ResolveAsync(string token)
    {
        try
        {
            using var client = this.httpClientFactory is not null
                ? this.httpClientFactory.CreateClient(nameof(GitHubIdentityResolver))
                : new HttpClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubUserEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Phantom.Workspaces", null));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "GitHub identity resolution returned HTTP {StatusCode}; skipping user-account upsert.",
                    (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("login", out var loginElement)
                && loginElement.ValueKind == JsonValueKind.String)
            {
                return loginElement.GetString();
            }

            this.logger.LogWarning("GitHub user endpoint did not return a 'login' field; skipping user-account upsert.");
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            this.logger.LogWarning(ex, "GitHub identity resolution failed; skipping user-account upsert.");
            return null;
        }
    }
}

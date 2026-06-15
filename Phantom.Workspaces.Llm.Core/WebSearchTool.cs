using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Configuration for web search tool.
/// </summary>
public sealed class WebSearchToolConfiguration
{
    /// <summary>
    /// Default web search provider when none is specified in the tool call.
    /// Defaults to Placeholder for testing.
    /// </summary>
    public WebSearchProvider DefaultSearchProvider { get; init; } = WebSearchProvider.Placeholder;

    /// <summary>
    /// API key for Bing Search API.
    /// </summary>
    public string? BingSearchApiKey { get; init; }

    /// <summary>
    /// API key for Google Custom Search.
    /// </summary>
    public string? GoogleSearchApiKey { get; init; }

    /// <summary>
    /// Custom Search Engine ID for Google Custom Search.
    /// </summary>
    public string? GoogleSearchEngineId { get; init; }
}

/// <summary>
/// Tool for executing web searches.
/// Supports Bing, Google, and Placeholder (test) providers.
/// </summary>
public sealed class WebSearchTool : AIFunction
{
    private static readonly JsonElement InputJsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query text."
            },
            "provider": {
              "type": "string",
              "description": "Optional provider name. Supported values: Placeholder, Bing, Google."
            }
          },
          "required": [ "query" ],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly HttpClient httpClient;
    private readonly WebSearchToolConfiguration configuration;
    private readonly ILogger? logger;

    public WebSearchTool(
        HttpClient? httpClient = null,
        WebSearchToolConfiguration? configuration = null,
        ILogger? logger = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.configuration = configuration ?? new WebSearchToolConfiguration();
        this.logger = logger;
    }

    public override string Name => "web_search";
    public override string Description =>
        "Search the web for information. Required: query (string). Optional: provider (Placeholder|Bing|Google).";
    public override JsonElement JsonSchema => InputJsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = ExtractString(arguments, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return "web_search requires a 'query' parameter.";
            }

            return await ExecuteWebSearchAsync(query, arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return "Web search was cancelled.";
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Web search execution failed");
            return $"Web search failed. {exception.Message}";
        }
    }

    private async Task<JsonElement> ExecuteWebSearchAsync(
        string query,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var providerName = ExtractString(arguments, "provider");
        var provider = string.IsNullOrWhiteSpace(providerName)
            ? this.configuration.DefaultSearchProvider
            : Enum.TryParse<WebSearchProvider>(providerName, ignoreCase: true, out var parsed)
                ? parsed
                : this.configuration.DefaultSearchProvider;

        logger?.LogTrace("Executing web_search with query='{Query}', provider={Provider}", query, provider);

        var results = provider switch
        {
            WebSearchProvider.Bing => await SearchBingAsync(query, cancellationToken),
            WebSearchProvider.Google => await SearchGoogleAsync(query, cancellationToken),
            WebSearchProvider.Placeholder => GeneratePlaceholderResults(query),
            _ => GeneratePlaceholderResults(query),
        };

        return JsonSerializer.SerializeToElement(new
        {
            provider = provider.ToString(),
            query,
            results = results.Select(static result => new
            {
                url = result.Url,
                title = result.Title,
                snippet = result.Snippet,
            }),
        });
    }

    private async Task<List<SearchResult>> SearchBingAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(this.configuration.BingSearchApiKey))
        {
            logger?.LogWarning("Bing Search API key not configured, falling back to placeholder");
            return GeneratePlaceholderResults(query);
        }

        var searchUrl = $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count=10";

        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", this.configuration.BingSearchApiKey);

        using var response = await this.httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning("Bing Search API returned {StatusCode}", response.StatusCode);
            return GeneratePlaceholderResults(query);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = ParseBingResults(content);

        return results;
    }

    private async Task<List<SearchResult>> SearchGoogleAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(this.configuration.GoogleSearchApiKey) ||
            string.IsNullOrWhiteSpace(this.configuration.GoogleSearchEngineId))
        {
            logger?.LogWarning("Google Search API credentials not configured, falling back to placeholder");
            return GeneratePlaceholderResults(query);
        }

        var searchUrl = $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(query)}&key={this.configuration.GoogleSearchApiKey}&cx={this.configuration.GoogleSearchEngineId}";

        using var response = await this.httpClient.GetAsync(searchUrl, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning("Google Search API returned {StatusCode}", response.StatusCode);
            return GeneratePlaceholderResults(query);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = ParseGoogleResults(content);

        return results;
    }

    private List<SearchResult> ParseBingResults(string jsonContent)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("webPages", out var webPages) &&
                webPages.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray().Take(10))
                {
                    var result = new SearchResult
                    {
                        Title = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                        Snippet = item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "",
                    };

                    if (!string.IsNullOrWhiteSpace(result.Url))
                    {
                        results.Add(result);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Bing search results");
        }

        return results;
    }

    private List<SearchResult> ParseGoogleResults(string jsonContent)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray().Take(10))
                {
                    var result = new SearchResult
                    {
                        Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        Url = item.TryGetProperty("link", out var link) ? link.GetString() ?? "" : "",
                        Snippet = item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "",
                    };

                    if (!string.IsNullOrWhiteSpace(result.Url))
                    {
                        results.Add(result);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Google search results");
        }

        return results;
    }

    private List<SearchResult> GeneratePlaceholderResults(string query)
    {
        return new List<SearchResult>
        {
            new SearchResult
            {
                Title = $"Placeholder result 1 for '{query}'",
                Url = $"https://example.com/search?q={Uri.EscapeDataString(query)}&result=1",
                Snippet = $"This is a placeholder search result for the query '{query}'. In production, this would contain actual search results from Bing or Google.",
            },
            new SearchResult
            {
                Title = $"Placeholder result 2 for '{query}'",
                Url = $"https://example.com/search?q={Uri.EscapeDataString(query)}&result=2",
                Snippet = $"Another placeholder result demonstrating web search capability for '{query}'.",
            },
        };
    }

    private static string? ExtractString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string str => str,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
    }

    private sealed class SearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }
}

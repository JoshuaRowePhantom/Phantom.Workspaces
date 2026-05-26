using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Configuration for web request tool.
/// </summary>
public sealed class WebRequestToolConfiguration
{
    // Currently no specific configuration needed, but provided for future extensibility
}

/// <summary>
/// Tool for executing HTTP requests with binary content support.
/// Handles text content directly and base64-encodes binary content (images, PDFs, etc.).
/// </summary>
public sealed class WebRequestTool : AIFunction
{
    private static readonly JsonElement InputJsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Absolute URL to request."
            },
            "method": {
              "type": "string",
              "description": "HTTP method to use. Defaults to GET."
            },
            "headers": {
              "type": "object",
              "description": "Optional HTTP headers. Overrides default request headers by header name.",
              "additionalProperties": {
                "anyOf": [
                  { "type": "string" },
                  { "type": "number" },
                  { "type": "integer" },
                  { "type": "boolean" }
                ]
              }
            }
          },
          "required": [ "url" ],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private static readonly IReadOnlyDictionary<string, string> DefaultHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Mozilla/5.0 (compatible; PhantomWorkspacesBot/1.0)",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5",
            ["Accept-Language"] = "en-US,en;q=0.9",
        };

    private readonly HttpClient httpClient;
    private readonly ILogger? logger;

    public WebRequestTool(
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.logger = logger;
    }

    public override string Name => "web_request";
    public override string Description =>
        "Fetch a URL from the web. Required: url (string). Optional: method (string, default GET), headers (object). " +
        "Sends browser-like defaults (User-Agent, Accept, Accept-Language); call headers override defaults.";
    public override JsonElement JsonSchema => InputJsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = ExtractString(arguments, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return new TextContent("web_request requires a 'url' parameter.");
            }

            return await ExecuteWebRequestAsync(url, arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new TextContent("Web request was cancelled.");
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Web request execution failed");
            return new TextContent($"Web request failed. {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<AIContent>> ExecuteWebRequestAsync(
        string url,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var method = ExtractString(arguments, "method")?.ToUpperInvariant() ?? "GET";
        var customHeaders = ExtractHeaders(arguments);

        logger?.LogTrace("Executing web_request: {Method} {Url}", method, url);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        ApplyHeaders(request, DefaultHeaders);
        if (customHeaders is not null)
        {
            ApplyHeaders(request, customHeaders);
        }

        using var response = await this.httpClient.SendAsync(request, cancellationToken);

        var responseHeaders = new Dictionary<string, string>();
        foreach (var (name, values) in response.Headers)
        {
            responseHeaders[name] = string.Join(", ", values);
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            responseHeaders[name] = string.Join(", ", values);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
        var isBinaryContent = IsBinaryMediaType(mediaType);

        AIContent bodyContent;
        if (isBinaryContent)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            bodyContent = new DataContent(bytes, mediaType);
        }
        else
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            bodyContent = new TextContent(text);
        }

        bodyContent.AdditionalProperties ??= [];
        bodyContent.AdditionalProperties["statusCode"] = (int)response.StatusCode;
        bodyContent.AdditionalProperties["statusReason"] = response.ReasonPhrase;
        bodyContent.AdditionalProperties["headers"] = responseHeaders;

        return [bodyContent];
    }

    private static bool IsBinaryMediaType(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        var lowerMediaType = mediaType.ToLowerInvariant();

        return lowerMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || lowerMediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || lowerMediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || lowerMediaType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || lowerMediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);
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

    private static IReadOnlyDictionary<string, string>? ExtractHeaders(
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("headers", out var value) || value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> objectHeaders)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (headerName, headerValue) in objectHeaders)
            {
                if (string.IsNullOrWhiteSpace(headerName) || headerValue is null)
                {
                    continue;
                }

                result[headerName] = headerValue.ToString() ?? string.Empty;
            }

            return result;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null ||
                    property.Value.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }

                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return result;
        }

        return null;
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (headerName, headerValue) in headers)
        {
            if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(headerValue))
            {
                continue;
            }

            if (headerName.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.TryParseAdd(headerValue);
                continue;
            }

            request.Headers.Remove(headerName);
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
    }
}

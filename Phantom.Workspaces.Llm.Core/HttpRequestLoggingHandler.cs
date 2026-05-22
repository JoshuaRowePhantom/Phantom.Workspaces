using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

internal sealed class HttpRequestLoggingHandler(ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = await ReadContentAsync(request.Content, cancellationToken);
        logger.LogTrace(
            "HTTP request: {Method} {RequestUri}\nRequest headers:\n{RequestHeaders}\nRequest body:\n{RequestBody}",
            request.Method,
            request.RequestUri,
            FormatHeaders(request.Headers, request.Content?.Headers),
            requestBody);

        var response = await base.SendAsync(request, cancellationToken);
        logger.LogTrace(
            "HTTP response: {StatusCode} {ReasonPhrase}\nResponse headers:\n{ResponseHeaders}",
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            FormatHeaders(response.Headers, response.Content?.Headers));

        return response;
    }

    private static async Task<string> ReadContentAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return "(none)";
        }

        var body = await content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(body) ? "(empty)" : body;
    }

    private static string FormatHeaders(HttpHeaders headers, HttpContentHeaders? contentHeaders)
    {
        var lines = headers.Select(header => $"{header.Key}: {string.Join(", ", header.Value)}").ToList();
        if (contentHeaders is not null)
        {
            lines.AddRange(contentHeaders.Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));
        }

        return lines.Count == 0 ? "(none)" : string.Join(Environment.NewLine, lines);
    }
}

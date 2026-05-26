using System.Net.Http.Headers;
using System.Text;
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

        await EnableResponseStreamingLogsAsync(response, cancellationToken);

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

    private async Task EnableResponseStreamingLogsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return;
        }

        if (!IsLikelyTextContent(response.Content.Headers.ContentType?.MediaType))
        {
            return;
        }

        var originalContent = response.Content;
        var stream = await originalContent.ReadAsStreamAsync(cancellationToken);
        var loggingStream = new HttpResponseLoggingStream(stream, logger);
        var wrappedContent = new StreamContent(loggingStream);

        foreach (var header in originalContent.Headers)
        {
            wrappedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = wrappedContent;
    }

    private static bool IsLikelyTextContent(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HttpResponseLoggingStream(Stream innerStream, ILogger logger) : Stream
    {
        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => innerStream.CanWrite;
        public override long Length => innerStream.Length;
        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush() => innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = innerStream.Read(buffer, offset, count);
            LogChunk(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await innerStream.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                var segment = buffer.Span[..read];
                logger.LogTrace("HTTP response stream chunk:\n{ResponseChunk}", Encoding.UTF8.GetString(segment));
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerStream.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync();
            await base.DisposeAsync();
        }

        private void LogChunk(byte[] buffer, int offset, int read)
        {
            if (read <= 0)
            {
                return;
            }

            logger.LogTrace(
                "HTTP response stream chunk:\n{ResponseChunk}",
                Encoding.UTF8.GetString(buffer, offset, read));
        }
    }
}

using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

internal sealed class HttpRequestLoggingHandler(ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method switch
        {
            "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" => request.Method.Method,
            _ => "OTHER",
        };
        logger.LogDebug("HTTP request: {Method}; request-bytes {Bytes}; outcome started.",
            method, Math.Clamp(request.Content?.Headers.ContentLength ?? 0, 0, 1_048_576));

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("HTTP response: {StatusCode}; method {Method}; outcome received.",
                (int)response.StatusCode, method);
            await EnableResponseStreamingLogsAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("HTTP request: {Method}; outcome cancelled.", method);
            throw;
        }
        catch (HttpRequestException)
        {
            logger.LogDebug("HTTP request: {Method}; outcome transport-failure.", method);
            throw;
        }
    }

    private async Task EnableResponseStreamingLogsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return;
        }

        var originalContent = response.Content;
        var stream = await originalContent.ReadAsStreamAsync(cancellationToken);
        var loggingStream = new HttpResponseLoggingStream(stream, logger, originalContent);
        var wrappedContent = new StreamContent(loggingStream);

        foreach (var header in originalContent.Headers)
        {
            wrappedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = wrappedContent;
    }

    private sealed class HttpResponseLoggingStream(Stream innerStream, ILogger logger, HttpContent originalContent) : Stream
    {
        private int disposed;
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
            LogChunk(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await innerStream.ReadAsync(buffer, cancellationToken);
            LogChunk(read);

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                innerStream.Dispose();
                originalContent.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await innerStream.DisposeAsync();
                originalContent.Dispose();
            }
            await base.DisposeAsync();
        }

        private void LogChunk(int read)
        {
            if (read <= 0)
            {
                return;
            }

            logger.LogDebug("HTTP response stream chunk; bytes {Bytes}; outcome read.", read);
        }
    }
}

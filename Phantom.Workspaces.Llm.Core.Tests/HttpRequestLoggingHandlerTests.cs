using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class HttpRequestLoggingHandlerTests
{
    [Fact]
    public async Task SendAsync_SensitiveStreamingResponse_LogsMetadataWithoutReadingPayload()
    {
        var sink = new LogSink();
        var logger = new TestLogger(sink);
        var handler = new HttpRequestLoggingHandler(logger)
        {
            InnerHandler = new StubHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("secret-stream-payload", Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("X-Test-Header", "ok");
                return response;
            }),
        };

        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://private-host.invalid/private-path?token=secret")
        {
            Content = new StringContent("{\"prompt\":\"private-prompt\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Private", "private-credential");

        using var response = await invoker.SendAsync(request, CancellationToken.None);
        Assert.Equal("secret-stream-payload", await response.Content.ReadAsStringAsync());

        Assert.Contains(sink.Messages, message => message.Contains("HTTP request: POST", StringComparison.Ordinal));
        Assert.Contains(sink.Messages, message => message.Contains("HTTP response: 200", StringComparison.Ordinal));
        Assert.Contains(sink.Messages, message => message.Contains("chunk", StringComparison.Ordinal));
        foreach (var message in sink.Messages)
            foreach (var secret in new[] { "private-host", "private-path", "secret", "private-prompt", "private-credential", "X-Test-Header" })
                Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_CancelledRequest_LogsOnlySafeOutcome()
    {
        var sink = new LogSink();
        var handler = new HttpRequestLoggingHandler(new TestLogger(sink))
        {
            InnerHandler = new StubHttpMessageHandler(
                _ => throw new OperationCanceledException("private-cancellation-payload")),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(
            new HttpMethod("PRIVATE-SECRET-METHOD"), "https://private-host.invalid/secret");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains(sink.Messages, message => message.Contains("outcome cancelled", StringComparison.Ordinal));
        Assert.All(sink.Messages, message =>
        {
            Assert.DoesNotContain("private-cancellation-payload", message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-host", message, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE-SECRET-METHOD", message, StringComparison.Ordinal);
        });
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(createResponse(request));
    }

    private sealed class LogSink
    {
        public List<string> Messages { get; } = [];
    }

    private sealed class TestLogger(LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Messages.Add(formatter(state, exception));
        }
    }
}

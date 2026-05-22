using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class HttpRequestLoggingHandlerTests
{
    [Fact]
    public async Task SendAsync_LogsRequestAndResponse()
    {
        var sink = new LogSink();
        var logger = new TestLogger(sink);
        var handler = new HttpRequestLoggingHandler(logger)
        {
            InnerHandler = new StubHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("X-Test-Header", "ok");
                return response;
            }),
        };

        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var _ = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains(sink.Messages, message => message.Contains("HTTP request: POST http://localhost:11434/api/chat", StringComparison.Ordinal));
        Assert.Contains(sink.Messages, message => message.Contains("\"prompt\":\"hello\"", StringComparison.Ordinal));
        Assert.Contains(sink.Messages, message => message.Contains("HTTP response: 200 OK", StringComparison.Ordinal));
        Assert.Contains(sink.Messages, message => message.Contains("X-Test-Header: ok", StringComparison.Ordinal));
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

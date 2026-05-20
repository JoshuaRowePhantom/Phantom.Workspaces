using System.Net;
using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Llm.Provider.Llama;

namespace Phantom.Workspaces.Llm.Provider.Llama.Tests;

public sealed class OllamaHttpLlmProviderTests
{
    [Fact]
    public async Task StreamAsync_DelegatesToStreamProviderUsingHttpResponseStream()
    {
        var handler = new QueueHandler(
            """
            {"model":"qwen3.6","created_at":"2026-05-19T23:46:55.9906659Z","response":"It","done":true,"done_reason":"length"}
            """);
        using var client = new HttpClient(handler);
        var provider = new OllamaHttpLlmProvider(
            client,
            new OllamaOptions
            {
                Model = "qwen3.6",
                Endpoint = new Uri(OllamaOptions.LocalEndpoint),
                ThinkingLevel = OllamaThinkingLevel.False,
                ContextSize = 32768,
            });

        var events = await ReadAllAsync(provider);
        var requestJson = handler.RequestBody!;
        using var requestDoc = JsonDocument.Parse(requestJson);

        Assert.Single(events);
        Assert.Equal("It", events[0].Event!.Content);
        Assert.Equal(LlmEventKinds.Turn, events[0].Event!.EventKind);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/chat", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("qwen3.6", requestDoc.RootElement.GetProperty("model").GetString());
        Assert.False(requestDoc.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(32768, requestDoc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task StreamAsync_UsesStringThinkingLevelWhenConfigured()
    {
        var handler = new QueueHandler(
            """
            {"model":"qwen3.6","created_at":"2026-05-19T23:46:55.9906659Z","response":"It","done":true,"done_reason":"length"}
            """);
        using var client = new HttpClient(handler);
        var provider = new OllamaHttpLlmProvider(
            client,
            new OllamaOptions
            {
                Model = "qwen3.6",
                ThinkingLevel = OllamaThinkingLevel.Medium,
            });

        _ = await ReadAllAsync(provider);

        var requestJson = handler.RequestBody!;
        using var requestDoc = JsonDocument.Parse(requestJson);
        Assert.Equal("medium", requestDoc.RootElement.GetProperty("think").GetString());
        Assert.False(requestDoc.RootElement.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task StreamAsync_StripsThinkingMessagesFromConversationPayload()
    {
        var handler = new QueueHandler(
            """
            {"model":"qwen3.6","created_at":"2026-05-19T23:46:55.9906659Z","response":"ok","done":true,"done_reason":"stop"}
            """);
        using var client = new HttpClient(handler);
        var provider = new OllamaHttpLlmProvider(
            client,
            new OllamaOptions
            {
                Model = "qwen3.6",
            });

        var conversation = LlmConversation.Create(
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "Hello",
                },
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.Assistant,
                    Thinking = "internal thought",
                },
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.Assistant,
                    Content = "Visible reply",
                    Thinking = "should not be forwarded",
                },
            ]);

        _ = await ReadAllAsync(provider, conversation);

        var requestJson = handler.RequestBody!;
        using var requestDoc = JsonDocument.Parse(requestJson);
        var messages = requestDoc.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Visible reply", messages[1].GetProperty("content").GetString());
        Assert.False(messages[1].TryGetProperty("thinking", out _));
    }

    private static async Task<List<LlmStreamEvent>> ReadAllAsync(
        OllamaHttpLlmProvider provider,
        LlmConversation? conversation = null)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(conversation ?? LlmConversation.Create()))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly string response;

        public QueueHandler(string response)
        {
            this.response = response;
        }

        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            this.RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var content = new StringContent(this.response, Encoding.UTF8, "application/json");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };

            return Task.FromResult(response);
        }
    }
}

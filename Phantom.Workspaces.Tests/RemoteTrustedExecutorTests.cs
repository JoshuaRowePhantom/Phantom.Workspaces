using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Trust;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteTrustedExecutorTests
{
    // AIJsonUtilities.DefaultOptions uses WriteIndented = true; NDJSON requires compact (single-line) JSON.
    private static readonly System.Text.Json.JsonSerializerOptions CompactAiOptions =
        new System.Text.Json.JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    [AvaloniaFact]
    public void Constructor_LocalInstance_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new RemoteTrustedExecutor(".", "https://remote.example/"));
    }

    [AvaloniaFact]
    public void CanExecute_MatchesConfiguredInstanceOnly()
    {
        var executor = new RemoteTrustedExecutor("remote-a", "https://remote.example/");

        Assert.True(executor.CanExecute("remote-a"));
        Assert.False(executor.CanExecute("remote-b"));
        Assert.False(executor.CanExecute("."));
    }

    [AvaloniaFact]
    public void Selector_PrefersRemoteExecutorForRemoteInstance()
    {
        var definition = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a"],
        };
        var profile = TrustProfileComposer.Compose([definition]);
        var remote = new RemoteTrustedExecutor("remote-a", "https://remote.example/");
        var selector = new TrustedExecutorSelector([new LocalTrustedExecutor(), remote]);

        var selected = selector.SelectExecutor(profile, "remote-a");

        Assert.Same(remote, selected);
    }

    [AvaloniaFact]
    public async Task WebRemoteChatClient_PostsToAgentEndpoint_ReturnsResponse()
    {
        var cannedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "remote-hello"));
        string? requestedPath = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            var json = JsonSerializer.Serialize(cannedResponse, AIJsonUtilities.DefaultOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        using var chatClient = new WebRemoteChatClient(
            "https://remote.example/",
            "{\"kind\":\"prompt\",\"name\":\"a\"}",
            httpClient: httpClient);

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("/agent/respond", requestedPath);
        Assert.Equal("remote-hello", response.Text);
    }

    [AvaloniaFact]
    public async Task WebRemoteChatClient_Streaming_YieldsRemoteResponse()
    {
        var cannedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "streamed-remote"));
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var json = JsonSerializer.Serialize(cannedResponse, AIJsonUtilities.DefaultOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        using var chatClient = new WebRemoteChatClient(
            "https://remote.example/",
            "{\"kind\":\"prompt\",\"name\":\"a\"}",
            httpClient: httpClient);

        var aggregated = new StringBuilder();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            aggregated.Append(update.Text);
        }

        Assert.Equal("streamed-remote", aggregated.ToString());
    }

    [AvaloniaFact]
    public async Task WebRemoteChatClient_NonSuccessStatus_Throws()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        using var chatClient = new WebRemoteChatClient(
            "https://remote.example/",
            "{\"kind\":\"prompt\",\"name\":\"a\"}",
            httpClient: httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task RemoteTrustedExecutor_OpenStreamAsync_WrongInstance_Throws()
    {
        var executor = new RemoteTrustedExecutor("remote-a", "https://remote.example/");
        var request = new TrustedStreamRequest
        {
            TargetClientInstance = "remote-b",
            StreamKind = "shell",
            OpenPayload = JsonDocument.Parse("{}").RootElement,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.OpenStreamAsync(request));
    }

    [AvaloniaFact]
    public async Task RemoteAgentChatClient_PostsToAgentChatTurnEndpoint_StreamsNdjsonResponse()
    {
        var update1 = new ChatResponseUpdate(ChatRole.Assistant, "hello");
        var update2 = new ChatResponseUpdate(ChatRole.Assistant, " world");
        var ndjson = JsonSerializer.Serialize(update1, CompactAiOptions) + "\n"
                   + JsonSerializer.Serialize(update2, CompactAiOptions) + "\n";

        string? requestedPath = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        using var client = new RemoteAgentChatClient(
            "https://remote.example/",
            "{\"kind\":\"prompt\",\"name\":\"a\"}",
            "session-42",
            httpClient: httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(u);
        }

        Assert.Equal("/agent/chat/session-42/turn", requestedPath);
        Assert.Equal(2, updates.Count);
        Assert.Equal("hello world", string.Concat(updates.Select(static u => u.Text)));
    }

    [AvaloniaFact]
    public async Task RemoteAgentChatClient_NonSuccessStatus_Throws()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        using var client = new RemoteAgentChatClient(
            "https://remote.example/",
            "{\"kind\":\"prompt\",\"name\":\"a\"}",
            "session-x",
            httpClient: httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task RemoteTrustedExecutor_CreateAgentChat_SendsToAgentChatTurnEndpoint()
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant, "remote-reply");
        var ndjson = JsonSerializer.Serialize(update, CompactAiOptions) + "\n";

        string? requestedPath = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        var executor = new RemoteTrustedExecutor("remote-a", "https://remote.example/", httpClient: httpClient);

        await using var chat = await executor.CreateAgentChatAsync(new TrustedExecutionRequest
        {
            AgentDefinition = AgentSchema.AgentDefinition.FromJson(
                """{ "kind":"prompt","name":"x","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[] }"""),
            TrustProfile = new TrustProfile { HostingWorkspacesClientInstances = ["remote-a"] },
            TargetClientInstance = "remote-a",
            AgentSessionId = "session-99",
        });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in chat.RunSingleTurnAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(u);
        }

        Assert.Equal("/agent/chat/session-99/turn", requestedPath);
        Assert.NotEmpty(updates);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(this.responder(request, cancellationToken));
    }
}
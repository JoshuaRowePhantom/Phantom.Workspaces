using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.Web.Client.Tests;

public sealed class WebClientAgentPersistenceStoreTests
{
    [Fact]
    public async Task StoreAsync_PostsToStoreEndpoint()
    {
        HttpRequestMessage? observedRequest = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            observedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        var agent = new PersistedAgent
        {
            AgentSessionId = "test-session-id",
            AgentSessionJson = null,
            AgentDefinitionJson = null,
            CopilotSdkSessionId = "copilot-session-id",
        };
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "test message"),
        };

        await store.StoreAsync(new StoreRequestAgent { Agent = agent, NewMessages = messages });

        Assert.NotNull(observedRequest);
        Assert.Equal(HttpMethod.Post, observedRequest!.Method);
        Assert.Equal("https://example.test/agent/persistence/store", observedRequest.RequestUri!.ToString());

        var requestBody = await observedRequest.Content!.ReadAsStringAsync();
        var deserializedRequest = JsonSerializer.Deserialize<StoreAgentRequest>(requestBody, AIJsonUtilities.DefaultOptions);
        Assert.NotNull(deserializedRequest);
        Assert.Equal("test-session-id", deserializedRequest!.Agent.AgentSessionId);
        Assert.Equal("copilot-session-id", deserializedRequest.Agent.CopilotSdkSessionId);
        Assert.Single(deserializedRequest.NewMessages!);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsAgent()
    {
        var responseDto = new PersistedAgentDto
        {
            AgentSessionId = "restored-session-id",
            AgentSessionJson = JsonDocument.Parse("{\"key\":\"value\"}").RootElement,
            AgentDefinitionJson = null,
            CopilotSdkSessionId = "restored-copilot-id",
        };
        var handler = new RecordingHttpMessageHandler(
            _ => JsonResponse(responseDto));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        var result = await store.RestoreAsync(new RestoreRequest { AgentSessionId = "restored-session-id" });

        Assert.NotNull(result);
        Assert.Equal("restored-session-id", result.Value.AgentSessionId);
        Assert.Equal("restored-copilot-id", result.Value.CopilotSdkSessionId);
        Assert.NotNull(result.Value.AgentSessionJson);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        var result = await store.RestoreAsync(new RestoreRequest { AgentSessionId = "missing-session-id" });

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMessagesAsync_ReturnsMessages()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "user message"),
            new ChatMessage(ChatRole.Assistant, "assistant response"),
        };
        var response = new ReadMessagesResponse { Messages = messages };
        var handler = new RecordingHttpMessageHandler(
            _ => JsonResponse(response));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        var result = await store.ReadMessagesAsync(new ReadMessagesRequest { AgentSessionId = "test-session-id" });

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
    }

    [Fact]
    public async Task AddSubAgentLinkAsync_PostsLink()
    {
        HttpRequestMessage? observedRequest = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            observedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        await store.AddSubAgentLinkAsync("parent-id", "child-id");

        Assert.NotNull(observedRequest);
        Assert.Equal(HttpMethod.Post, observedRequest!.Method);
        Assert.Equal("https://example.test/agent/persistence/sub-agent-links/add", observedRequest.RequestUri!.ToString());

        var requestBody = await observedRequest.Content!.ReadAsStringAsync();
        var deserializedRequest = JsonSerializer.Deserialize<AddSubAgentLinkRequest>(requestBody, AIJsonUtilities.DefaultOptions);
        Assert.NotNull(deserializedRequest);
        Assert.Equal("parent-id", deserializedRequest!.ParentSessionId);
        Assert.Equal("child-id", deserializedRequest.ChildSessionId);
    }

    [Fact]
    public async Task ReadSubAgentChildIdsAsync_ReturnsChildIds()
    {
        var response = new ReadSubAgentChildIdsResponse
        {
            ChildSessionIds = ["child-1", "child-2", "child-3"],
        };
        var handler = new RecordingHttpMessageHandler(
            _ => JsonResponse(response));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var store = new WebClientAgentPersistenceStore("https://example.test", httpClient: httpClient);

        var result = await store.ReadSubAgentChildIdsAsync("parent-id");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("child-1", result[0].Value);
        Assert.Equal("child-2", result[1].Value);
        Assert.Equal("child-3", result[2].Value);
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}

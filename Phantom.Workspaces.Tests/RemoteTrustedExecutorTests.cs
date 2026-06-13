using System;
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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class LocalReverseExecutionHandlerTests
{
    private const string EchoAgentJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    [Fact]
    public async Task ExecuteAsync_EchoAgent_StreamsEchoedText()
    {
        var handler = new LocalReverseExecutionHandler();
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            Messages = [new ChatMessage(ChatRole.User, "hello-reverse")],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in handler.ExecuteAsync(request, CancellationToken.None))
        {
            updates.Add(update);
        }

        var text = string.Concat(System.Linq.Enumerable.Select(updates, static u => u.Text));
        Assert.Contains("hello-reverse", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithSessionIdAndCache_RoutesToCache()
    {
        await using var cache = new AgentChatSessionCache();
        var handler = new LocalReverseExecutionHandler(sessionCache: cache);
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "test-session-cache",
            Messages = [new ChatMessage(ChatRole.User, "hello-via-cache")],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in handler.ExecuteAsync(request, CancellationToken.None))
        {
            updates.Add(update);
        }

        var text = string.Concat(System.Linq.Enumerable.Select(updates, static u => u.Text));
        Assert.Contains("hello-via-cache", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithSessionIdButNoCache_StatelessFallback()
    {
        var handler = new LocalReverseExecutionHandler(sessionCache: null);
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "ignored-session-id",
            Messages = [new ChatMessage(ChatRole.User, "hello-stateless")],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in handler.ExecuteAsync(request, CancellationToken.None))
        {
            updates.Add(update);
        }

        var text = string.Concat(System.Linq.Enumerable.Select(updates, static u => u.Text));
        Assert.Contains("hello-stateless", text);
    }
}

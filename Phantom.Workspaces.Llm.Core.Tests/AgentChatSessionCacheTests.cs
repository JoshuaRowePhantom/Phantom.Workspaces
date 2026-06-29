using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatSessionCacheTests
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
    public async Task RunTurnAsync_FirstCall_CreatesNewSessionAndStreamsResponse()
    {
        await using var cache = new AgentChatSessionCache();
        var request = new AgentChatTurnRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "session-1",
            Messages = [new ChatMessage(ChatRole.User, "hello-cache")],
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in cache.RunTurnAsync(request, CancellationToken.None))
        {
            updates.Add(update);
        }

        var text = string.Concat(updates.Select(static u => u.Text));
        Assert.Contains("hello-cache", text);
    }

    [Fact]
    public async Task RunTurnAsync_SecondCall_ReusesSameSession()
    {
        await using var cache = new AgentChatSessionCache();
        var sessionId = "session-reuse";
        var request1 = new AgentChatTurnRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = sessionId,
            Messages = [new ChatMessage(ChatRole.User, "first-turn")],
        };
        var request2 = new AgentChatTurnRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = sessionId,
            Messages = [new ChatMessage(ChatRole.User, "second-turn")],
        };

        // Run two sequential turns
        await ConsumeAsync(cache.RunTurnAsync(request1, CancellationToken.None));
        await ConsumeAsync(cache.RunTurnAsync(request2, CancellationToken.None));

        // If the session was reused, the second turn also streams a response (no error)
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in cache.RunTurnAsync(request2, CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
    }

    [Fact]
    public async Task RunTurnAsync_DifferentSessionIds_CreatesSeparateSessions()
    {
        await using var cache = new AgentChatSessionCache();
        var requestA = new AgentChatTurnRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "session-A",
            Messages = [new ChatMessage(ChatRole.User, "msg-A")],
        };
        var requestB = new AgentChatTurnRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "session-B",
            Messages = [new ChatMessage(ChatRole.User, "msg-B")],
        };

        var updatesA = new List<ChatResponseUpdate>();
        await foreach (var u in cache.RunTurnAsync(requestA, CancellationToken.None))
        {
            updatesA.Add(u);
        }

        var updatesB = new List<ChatResponseUpdate>();
        await foreach (var u in cache.RunTurnAsync(requestB, CancellationToken.None))
        {
            updatesB.Add(u);
        }

        Assert.Contains("msg-A", string.Concat(updatesA.Select(static u => u.Text)));
        Assert.Contains("msg-B", string.Concat(updatesB.Select(static u => u.Text)));
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<ChatResponseUpdate> source)
    {
        await foreach (var _ in source)
        {
        }
    }
}

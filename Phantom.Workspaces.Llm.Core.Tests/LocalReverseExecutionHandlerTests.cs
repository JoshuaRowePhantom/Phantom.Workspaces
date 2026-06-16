using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
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
}

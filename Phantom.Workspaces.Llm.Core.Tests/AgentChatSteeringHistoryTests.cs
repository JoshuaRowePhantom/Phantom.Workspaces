using System.Linq;
using System.Runtime.CompilerServices;
using AgentSchema;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatSteeringHistoryTests
{
    private const string AgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    [Fact]
    public async Task SteeringMessagesInjectedByMiddleware_AreRecordedInHistory()
    {
        var queueManager = new AgentInputQueueManager();
        var middleware = new ToolResultSteeringMiddleware(new StubChatClient(), queueManager);

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(AgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = middleware,
            DisplayNameOverride = "test-chat",
        });

        queueManager.Enqueue(
            queueManager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "steer me")] }]);

        // The middleware injects queued steering messages at tool-result boundaries. Drive a model
        // call whose last message is a tool result so MessagesInjected fires synchronously and the
        // owning AgentChat records the steering message in its visible history (issue #17).
        await middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "result")])]);

        Assert.Contains(
            chat.History,
            item => item.Role == ChatRole.User
                && string.Concat(item.Contents.OfType<TextContent>().Select(content => content.Text)) == "steer me");
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

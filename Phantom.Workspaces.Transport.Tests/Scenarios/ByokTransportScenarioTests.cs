using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// BYOK coverage for Scenarios 3 and 4: the executor resolves its chat client via
/// <see cref="AgentFactory.CreateChatClient(AgentSchema.AgentDefinition)"/> against a BYOK endpoint
/// served by <see cref="ScriptedByokChatServer"/> (loopback, no ambient network), and the resulting
/// turn is carried over the transport. The chat client is never hand-constructed — it is produced by
/// the factory from an <c>AgentDefinition</c>, exactly as production does.
/// </summary>
public sealed class ByokTransportScenarioTests
{
    private static string ByokDefinitionJson(string baseUrl) => $$"""
    {
      "kind": "prompt",
      "name": "byok-scenario",
      "model": {
        "id": "gpt-test",
        "provider": "github-models",
        "connection": {
          "kind": "key",
          "endpoint": "{{baseUrl}}",
          "apiKey": "test-key"
        }
      }
    }
    """;

    private static IChatClient CreateByokExecutor(ScriptedByokChatServer server, string assistantText)
    {
        var conversation = server.AddConversation("main", request => request.AnyMessageContains("user", "byok-marker"));
        var stream = conversation.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, assistantText));
        stream.Complete();

        var definition = AgentDefinitionLoader.LoadAgentFromJson(ByokDefinitionJson(server.BaseUrl));
        return AgentFactory.CreateChatClient(definition).ChatClient;
    }

    [Fact]
    public async Task Scenario4_Byok_LocalTransport_FullTurn()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        var executor = CreateByokExecutor(server, "byok-local-pong");

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "byok-marker please", ct);

        Assert.Contains("byok-local-pong", text, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Scenario3_Byok_RemoteViaHubRelay_FullTurn()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        var executor = CreateByokExecutor(server, "byok-remote-pong");

        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "byok-marker please", ct);

        Assert.Contains("byok-remote-pong", text, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }
}

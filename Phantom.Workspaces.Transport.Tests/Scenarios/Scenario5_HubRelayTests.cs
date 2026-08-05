using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 5: a three-machine relay turn. Machine B reaches the executor on Machine C through the
/// hub on Machine A, over the real <c>ReverseHttpForwardingTransportFactory</c> /
/// <c>ReverseExecutionDispatcher</c> path. Coverage asserts both BYOK end-to-end behaviour (the
/// executor's chat client is produced by <see cref="AgentFactory"/> against a loopback BYOK
/// endpoint) and that the hub's relay pump is byte-transparent in both directions.
/// </summary>
public sealed class Scenario5_HubRelayTests
{
    private static string ByokDefinitionJson(string baseUrl) => $$"""
    {
      "kind": "prompt",
      "name": "scenario5-byok",
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

    [Fact]
    public async Task Scenario5_ThreeMachineRelay_ByokTurn_StreamsAcrossHub()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        var conversation = server.AddConversation("relay", request => request.AnyMessageContains("user", "relay-marker"));
        var stream = conversation.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "relay-"));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "pong"));
        stream.Complete();

        var definition = AgentDefinitionLoader.LoadAgentFromJson(ByokDefinitionJson(server.BaseUrl));
        var executor = AgentFactory.CreateChatClient(definition).ChatClient;

        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "relay-marker please", ct);

        Assert.Contains("relay-pong", text, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Scenario5_RelayPump_IsByteTransparentInBothDirections()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var fixture = new InProcessReverseHubFixture();
        var entityId = Guid.NewGuid();
        await fixture.SimulateClientRegistrationAsync(entityId, ct);
        var registrationChannel = fixture.LastClientRegistrationChannel!;
        var forwardingClient = await fixture.CreateForwardingClientAsync(ct);
        using var relayRequest = JsonDocument.Parse($$"""{"type":"reverse-http","entity-id":"{{entityId:D}}"}""");
        var relay = await forwardingClient.ConnectToMessageChannelAsync(relayRequest.RootElement, ct);

        // The hub acknowledges the relay before it begins pumping.
        var ack = await relay.Reader.ReadAsync(ct);
        Assert.Equal("channel-open-ack", ack.GetProperty("type").GetString());

        // Machine B -> Machine C: an arbitrarily-shaped frame is relayed byte-for-byte.
        using var bToC = JsonDocument.Parse(
            """{"type":"channel-open","channel-id":"7","request":{"type":"chat-client","payload":{"n":42,"s":"hi"}}}""");
        await relay.Writer.WriteAsync(bToC.RootElement.Clone(), ct);
        var onC = await registrationChannel.Reader.ReadAsync(ct);
        Assert.Equal(bToC.RootElement.GetRawText(), onC.GetRawText());

        // Machine C -> Machine B: the reverse direction is equally transparent.
        using var cToB = JsonDocument.Parse(
            """{"type":"channel-message","channel-id":"7","payload":{"bytes":[1,2,3],"text":"pong"}}""");
        await registrationChannel.Writer.WriteAsync(cToB.RootElement.Clone(), ct);
        var onB = await relay.Reader.ReadAsync(ct);
        Assert.Equal(cToB.RootElement.GetRawText(), onB.GetRawText());
    }
}

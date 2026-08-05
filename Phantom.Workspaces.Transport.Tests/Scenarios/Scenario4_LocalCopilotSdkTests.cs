using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 4: all-local Copilot-SDK-shaped turn. Structurally identical to Scenario 3 but with no
/// network boundary — every transport hop is a <see cref="LocalTransport"/>, so no serialization or
/// relay occurs. Validates that the same executor/listener wiring works when co-located.
/// </summary>
public sealed class Scenario4_LocalCopilotSdkTests
{
    [Fact]
    public async Task Scenario4_FullTurn_AllLocalTransport()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("local-", "copilot-", "turn");
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "run locally", ct);

        Assert.Equal("local-copilot-turn", text);
        Assert.Equal("run locally", Assert.Single(executor.LastRequestMessages).Text);
    }

    [Fact]
    public async Task Scenario4_SecondTurn_UsesFreshChannel()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = new DeterministicTestChatClient();
        var first = executor.EnqueueStreamingResponse();
        first.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "one"));
        first.Complete();
        var second = executor.EnqueueStreamingResponse();
        second.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "two"));
        second.Complete();

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);

        using (var clientOne = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest()))
        {
            Assert.Equal("one", await TransportScenarioSupport.RunTurnAsync(clientOne, "first", ct));
        }

        using var clientTwo = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());
        Assert.Equal("two", await TransportScenarioSupport.RunTurnAsync(clientTwo, "second", ct));
    }
}

using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 1: single-machine, local OpenAI-style streaming. A full turn flows over
/// <see cref="LocalTransport"/> (no network, no serialization boundary) from
/// <see cref="ChatClientOverTransport"/> through <see cref="ChatClientTransportListener"/> to a
/// deterministic streaming chat client, and every streamed update is surfaced back to the caller.
/// </summary>
public sealed class Scenario1_LocalOpenAiTests
{
    [Fact]
    public async Task Scenario1_FullTurn_StreamingUpdatesReachClient()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("Hello", ", ", "world");
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "hi", ct);

        Assert.Equal("Hello, world", text);
        Assert.Single(executor.LastRequestMessages);
        Assert.Equal("hi", executor.LastRequestMessages[0].Text);
    }

    [Fact]
    public async Task Scenario1_FullTurn_PreservesUpdateOrdering()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("1", "2", "3", "4");
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var updates = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "count")], null, ct))
        {
            updates.Add(update.Text);
        }

        Assert.Equal(["1", "2", "3", "4"], updates);
    }
}

using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 3: remote executor reached through the hub relay (Machine B → hub on Machine A →
/// Machine C). The turn is carried end-to-end over the real
/// <c>ReverseHttpForwardingTransportFactory</c> / <c>ReverseExecutionDispatcher</c> path; the
/// executor's chat behaviour is deterministic. (A fully hermetic Copilot-SDK turn additionally
/// requires the external Copilot CLI, which is exercised by the CLI-gated
/// <c>ScriptedByokChatServerTests</c>; here the transport contract is validated deterministically.)
/// </summary>
public sealed class Scenario3_RemoteCopilotSdkTests
{
    [Fact]
    public async Task Scenario3_FullTurn_ViaReverseHubRelay()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("remote-", "reply");
        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "please respond", ct);

        Assert.Equal("remote-reply", text);
        Assert.Single(executor.LastRequestMessages);
        Assert.Equal("please respond", executor.LastRequestMessages[0].Text);
    }

    [Fact]
    public async Task Scenario3_MultipleUpdates_StreamBackAcrossRelay()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("a", "b", "c");
        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var updates = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "stream")], null, ct))
        {
            updates.Add(update.Text);
        }

        Assert.Equal(["a", "b", "c"], updates);
    }
}

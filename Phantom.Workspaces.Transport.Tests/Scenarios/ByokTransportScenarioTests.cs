using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// BYOK coverage for Scenarios 3 and 4: the executor resolves its chat client via
/// <see cref="AgentFactory.CreateChatClientAsync"/> with a broker-backed credential reference against a BYOK endpoint
/// served by <see cref="ScriptedByokChatServer"/> (loopback, no ambient network), and the resulting
/// turn is carried over the transport. The chat client is never hand-constructed — it is produced by
/// the factory from an <c>AgentDefinition</c>, exactly as production does.
/// </summary>
public sealed class ByokTransportScenarioTests
{
    private static async Task<IChatClient> CreateByokExecutorAsync(
        ScriptedByokChatServer server, ByokScenarioCredentials credentials, string assistantText, CancellationToken ct)
    {
        var conversation = server.AddConversation("main", request => request.AnyMessageContains("user", "byok-marker"));
        var stream = conversation.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, assistantText));
        stream.Complete();

        return await credentials.CreateExecutorAsync(server.BaseUrl, ct);
    }

    [Fact]
    public async Task Scenario4_Byok_LocalTransport_FullTurn()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var executor = await CreateByokExecutorAsync(server, credentials, "byok-local-pong", ct);
        await using var executorLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(executor);

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "byok-marker please", ct);

        Assert.Contains("byok-local-pong", text, StringComparison.Ordinal);
        Assert.Single(credentials.Requests);
        Assert.Single(server.RecordedRequests);
        Assert.True(credentials.Validations > 1);
        Assert.True(credentials.GenerationChecks > 1);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Scenario3_Byok_RemoteViaHubRelay_FullTurn()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var executor = await CreateByokExecutorAsync(server, credentials, "byok-remote-pong", ct);
        await using var executorLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(executor);

        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "byok-marker please", ct);

        Assert.Contains("byok-remote-pong", text, StringComparison.Ordinal);
        Assert.Single(credentials.Requests);
        Assert.Single(server.RecordedRequests);
        Assert.True(credentials.Validations > 1);
        Assert.True(credentials.GenerationChecks > 1);
        Assert.Empty(server.Failures);
    }
}

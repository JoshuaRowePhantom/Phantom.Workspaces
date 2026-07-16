using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.ErrorPaths;

/// <summary>
/// Relay error paths: when Machine C crashes mid-turn its registration channel closes, the hub's
/// relay pump propagates the closure, and Machine B's in-flight
/// <see cref="ChatClientOverTransport.GetStreamingResponseAsync"/> terminates promptly with an
/// exception rather than hanging forever.
/// </summary>
public sealed class RelayErrorTests
{
    [Fact]
    public async Task RelayError_MachineCCrashMidTurn_MachineBObservesChannelClose()
    {
        var ct = TransportScenarioSupport.TestToken();

        // The executor streams one update, then stalls the turn (the second update is never marked
        // ready and the turn is never completed), holding the turn in flight across the crash.
        var executor = new DeterministicTestChatClient();
        var stream = executor.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "partial"));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "never"), isReady: false);

        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);
        var machineB = await harness.ConnectMachineBAsync(ct);
        using var over = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());
        await using var enumerator = over
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], null, ct)
            .GetAsyncEnumerator(ct);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("partial", enumerator.Current.Text);

        // Machine C crashes: its registration channel is torn down mid-turn.
        await harness.CrashExecutorAsync();

        // The relay pump closes Machine B's channel; continued enumeration throws instead of hanging.
        await Assert.ThrowsAnyAsync<Exception>(async () => await enumerator.MoveNextAsync());
    }
}

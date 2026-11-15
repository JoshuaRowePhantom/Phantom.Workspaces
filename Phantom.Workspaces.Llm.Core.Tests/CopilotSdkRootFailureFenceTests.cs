using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkRootFailureFenceTests
{
    [Fact]
    public void TranslatorCoverage_BelongsToTheOriginalReaderNotTheNextBatch()
    {
        var root = new CopilotSdkRootEventQueue();
        var first = root.Acquire(false);
        var state = root.TranslationFor(first);
        state.AnonymousRootMessageId = "original anonymous identity";
        root.Accept(new AssistantMessageDeltaEvent { Data = new() { MessageId = "A", DeltaContent = "draft" } });
        root.ReturnUnconsumed(first, true);
        var suffix = root.Acquire(true);
        Assert.Same(first, suffix);
        Assert.Same(state, root.TranslationFor(suffix));
        while (suffix.TryRead(out _)) { }
        root.Release(suffix);
        root.Accept(new AssistantMessageDeltaEvent { Data = new() { MessageId = "A", DeltaContent = "distinct batch" } });
        var next = root.Acquire(true);
        Assert.NotSame(state, root.TranslationFor(next));
        Assert.Null(root.TranslationFor(next).AnonymousRootMessageId);
        Assert.Empty(root.TranslationFor(next).RootTextByMessage);
        while (next.TryRead(out _)) { }
        root.Release(next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistenceFailure_TransfersItsFenceAcrossDrainAttachment(bool attachFirst)
    {
        var root = new CopilotSdkRootEventQueue();
        var reader = root.Acquire(false);
        var drain = new CopilotSdkOutputDrain();
        if (attachFirst) drain.Attach(root.SealAccepted, root.Fail);
        var original = new IOException("owned persistence failure");
        drain.RequestFailure(original);
        if (!attachFirst) drain.Attach(root.SealAccepted, root.Fail);
        Assert.True(drain.StopRequested);
        Assert.False(root.IsReceiving(reader));
        Assert.Same(original, Assert.Throws<IOException>(() => { _ = root.StartSteering(reader, () => true, () => Task.CompletedTask); }));
        root.Release(reader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeNativeStart_PreservesUnclaimedOrSettlesClaimedInput(bool afterClaim)
    {
        var root = new CopilotSdkRootEventQueue();
        var reader = root.Acquire(false);
        var queue = new AgentInputQueueManager();
        using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null, queueManager: queue);
        var original = new IOException("canonical root failed before native start");
        var sends = 0;
        using var subscription = sdk.SubscribeImmediateQueueSteering(
            (_, _) => root.StartSteering(reader, () => true, () => { sends++; return Task.CompletedTask; }),
            () => root.IsReceiving(reader));
        if (afterClaim)
            queue.QueueStateChanged += (_, change) =>
            {
                if (change.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemRemoved) root.Fail(original);
            };
        else root.Fail(original);
        var completion = new AgentInputTurnCompletion();
        queue.Enqueue(queue.ImmediateQueue,
            [new() { Messages = [new ChatMessage(ChatRole.User, "owned once")], TurnCompletion = completion }]);
        Assert.Equal(0, sends);
        if (afterClaim)
        {
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => completion.Settlement));
            Assert.Same(original, Assert.Single(await sdk.WaitForSteeringDeliveriesAsync()).Failure);
            Assert.Empty(queue.ImmediateQueue.Items);
        }
        else
        {
            Assert.False(completion.Settlement.IsCompleted);
            Assert.Same(completion, Assert.Single(queue.ImmediateQueue.Items).TurnCompletion);
            Assert.Empty(await sdk.WaitForSteeringDeliveriesAsync());
        }
        root.Release(reader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterNativeStart_DoesNotReclassifyAcceptedAcknowledgementOrReplay(bool nativeFails)
    {
        var root = new CopilotSdkRootEventQueue();
        var reader = root.Acquire(false);
        var queue = new AgentInputQueueManager();
        using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null, queueManager: queue);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        using var subscription = sdk.SubscribeImmediateQueueSteering(
            (_, _) => root.StartSteering(reader, () => true, () => { sends++; return ack.Task; }),
            () => root.IsReceiving(reader));
        var completion = new AgentInputTurnCompletion();
        queue.Enqueue(queue.ImmediateQueue,
            [new() { Messages = [new ChatMessage(ChatRole.User, "native start won")], TurnCompletion = completion }]);
        var rootFailure = new IOException("later root failure");
        var nativeFailure = new IOException("independent native acknowledgement failure");
        root.Fail(rootFailure);
        if (nativeFails) ack.SetException(nativeFailure);
        else ack.SetResult();
        if (nativeFails) Assert.Same(nativeFailure, await Assert.ThrowsAsync<IOException>(() => completion.Settlement));
        else await completion.Settlement;
        var outcome = Assert.Single(await sdk.WaitForSteeringDeliveriesAsync());
        Assert.Equal(!nativeFails, outcome.Delivered);
        Assert.Same(nativeFails ? nativeFailure : null, outcome.Failure);
        queue.Enqueue(queue.ImmediateQueue, [new() { Messages = [new ChatMessage(ChatRole.User, "retained")] }]);
        Assert.Single(queue.ImmediateQueue.Items);
        Assert.Equal(1, sends);
        root.Release(reader);
    }

    [Fact]
    public async Task FailedReceipt_DrainsBufferedOutputAndRetainsOriginalFailureWithoutAcceptingMore()
    {
        var root = new CopilotSdkRootEventQueue();
        var reader = root.Acquire(false);
        var accepted = new AssistantMessageDeltaEvent { Data = new() { MessageId = "accepted", DeltaContent = "owned" } };
        root.Accept(accepted);
        var original = new IOException("receipt failed");
        root.Fail(original);
        root.Accept(new AssistantMessageDeltaEvent { Data = new() { MessageId = "rejected", DeltaContent = "not accepted" } });
        var received = new List<SessionEvent>();
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var value in reader.ReadAllAsync()) received.Add(value);
        }));
        Assert.Same(accepted, Assert.Single(received));
        Assert.True(root.IsSealed);
        Assert.False(root.IsReceiving(reader));
        var fresh = new CopilotSdkRootEventQueue();
        Assert.True(fresh.IsReceiving(fresh.Acquire(false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceipt_FencesStreamingAndDirectRequestInvocation(bool direct)
    {
        var root = new CopilotSdkRootEventQueue();
        var reader = direct ? null : root.Acquire(false);
        if (direct) root.BeginDirectResponse();
        var original = new IOException("request reservation failed");
        root.Fail(original);
        var sends = 0;
        Assert.Same(original, Assert.Throws<IOException>(() =>
        {
            if (direct) root.StartDirectResponse(() => { sends++; return Task.FromResult(1); });
            else root.TryStartRequest(reader!, () => { sends++; return Task.CompletedTask; }, out _);
        }));
        Assert.Equal(0, sends);
        if (reader is not null) root.Release(reader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchCompletion_KeepsIndependentFailuresAndDoesNotDuplicateCausalWrappers(bool wrappedAggregate)
    {
        var root = new CopilotSdkRootEventQueue();
        var observer = new IOException("observer");
        var independent = new InvalidOperationException("independent dispatch");
        root.Fail(observer);
        Exception? observed = null;
        root.CompleteDispatch(error => observed = error, new InvalidOperationException("causal", observer));
        Assert.Same(observer, observed);
        var independentFailures = new AggregateException(observer, independent);
        root.CompleteDispatch(error => observed = error, wrappedAggregate
            ? new InvalidOperationException("aggregate wrapper", independentFailures) : independentFailures);
        Assert.Equal<Exception>([observer, independent], Assert.IsType<AggregateException>(observed).InnerExceptions);
    }
}

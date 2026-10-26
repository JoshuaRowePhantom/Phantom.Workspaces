using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteAgentQueuePublicationTests
{
    [Fact]
    public async Task AttachSnapshot_CoversDeferredQueueNotification_DoesNotPublishNonadvancingDelta()
    {
        var scheduler = new QueuedScheduler();
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue();
        manager.RegisterInputQueue(queue);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queue, scheduler);
        await using var lease = Lease(adapter);
        var cursor = new ReplayCursor { Epoch = lease.Epoch, Sequence = lease.Replay.HighWaterMark };
        var added = new AgentInputQueue();
        manager.RegisterInputQueue(added);
        adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "included in snapshot"));

        var initial = await lease.AttachAndCaptureInitialStateAsync(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "viewer",
            Channel = Mock.Of<IMessageChannel>(),
        }, TestContext.Current.CancellationToken);
        try
        {
            scheduler.Drain();

            Assert.Equal(adapter.Snapshot.Revision, initial.Snapshot!.InputQueues.Revision);
            Assert.DoesNotContain(lease.Replay.ReadAfter(cursor).Frames, frame => frame.Type == "queue-changed");

            Assert.True(manager.UnregisterInputQueue(added));
            scheduler.Drain();
            var delta = Assert.Single(lease.Replay.ReadAfter(cursor).Frames
                .Select(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize).OfType<QueueChangedEvent>());
            Assert.True(delta.Revision > initial.Snapshot.InputQueues.Revision);
            Assert.Equal([added.QueueId], delta.RemovedQueueIds);
            Assert.DoesNotContain(delta.Queues, q => q.QueueId == added.QueueId);
        }
        finally
        {
            // This test deliberately inspects a staged open without activating its publisher.
            await initial.Attachment.AbortOpenAsync();
        }
    }

    [Fact]
    public async Task ReentrantAggregateObserver_RemoteLeasePublishesCapturedSnapshotsInRevisionOrder()
    {
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue();
        manager.RegisterInputQueue(queue);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queue);
        var reentered = false;
        adapter.Changed += (_, _) =>
        {
            if (!reentered)
            {
                reentered = true;
                adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "second"));
            }
        };
        await using var lease = Lease(adapter);
        var cursor = new ReplayCursor { Epoch = lease.Epoch, Sequence = lease.Replay.HighWaterMark };

        adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "first"));

        var deltas = lease.Replay.ReadAfter(cursor).Frames
            .Select(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize).OfType<QueueChangedEvent>().ToArray();
        Assert.Equal(2, deltas.Length);
        Assert.True(deltas[0].Revision < deltas[1].Revision);
        Assert.Single(deltas[0].Queues.Single(q => q.IsDefault).Items);
        Assert.Equal(2, deltas[1].Queues.Single(q => q.IsDefault).Items.Length);
        Assert.Equal(adapter.Snapshot.Revision, deltas[1].Revision);
    }

    [Fact]
    public async Task FencedLease_PendingQueueNotification_DoesNotAppendReplay()
    {
        var scheduler = new QueuedScheduler();
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue();
        manager.RegisterInputQueue(queue);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queue, scheduler);
        var lease = Lease(adapter);
        adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "pending"));
        await lease.DisposeAsync();
        var terminalSequence = lease.Replay.HighWaterMark;

        scheduler.Drain();

        Assert.True(lease.IsFenced);
        Assert.Equal(terminalSequence, lease.Replay.HighWaterMark);
    }

    private static RemoteAgentSessionLease Lease(IAgentInputQueues queues)
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.InputQueues).Returns(queues);
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return new RemoteAgentSessionLease(
            "queue-session", 1, new RuntimeEpoch { Value = Guid.NewGuid() }, chat.Object,
            continueInBackground: true, () => new AgentSessionSnapshot
            {
                Information = new AgentInformation
                {
                    AgentSessionId = "queue-session", AgentId = "agent", Name = "agent", DisplayName = "Agent",
                    Description = "Queue publication test", AcceptsUserInput = true,
                    AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                        """{"kind":"prompt","name":"agent","model":{"id":"echo","provider":"echo","apiType":"Echo"}}"""),
                },
                Usage = new Usage(),
                InputQueues = queues.Snapshot,
                IsBusy = false,
                History = [],
                RunningItems = [],
                Tools = [],
                Subagents = [],
                Modals = [],
                ContinueInBackground = true,
                ViewerCount = 0,
            });
    }

    private static EnqueueAgentInputRequest Request(AgentInputQueue queue, long revision, string text) => new()
    {
        TargetQueueId = queue.QueueId,
        ExpectedRevision = revision,
        CommandId = Guid.NewGuid(),
        Messages = [new ChatMessage(ChatRole.User, text)],
    };

    private sealed class QueuedScheduler : TaskScheduler
    {
        private readonly Queue<Task> tasks = new();
        protected override IEnumerable<Task>? GetScheduledTasks() => this.tasks.ToArray();
        protected override void QueueTask(Task task) => this.tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        internal void Drain()
        {
            while (this.tasks.TryDequeue(out var task))
            {
                Assert.True(this.TryExecuteTask(task));
                Assert.Null(task.Exception);
            }
        }
    }
}

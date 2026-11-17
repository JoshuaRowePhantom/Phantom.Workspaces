using System.Reflection;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Theory]
    [InlineData(AgentInputQueueCommandStatus.Applied)]
    [InlineData(AgentInputQueueCommandStatus.Conflict)]
    public async Task InputQueues_AuthoritativeSnapshotCoversSeveralDeltas_AppliesOnlyNewerState(
        AgentInputQueueCommandStatus status)
    {
        var baseline = AgentSessionProtocolCodecTests.Snapshot();
        var removed = baseline.InputQueues.Queues[0] with { QueueId = "removed", IsDefault = false, IsImmediate = false };
        var (transport, chat) = await AttachAsync(baseline with
        {
            InputQueues = baseline.InputQueues with { Queues = baseline.InputQueues.Queues.Add(removed) },
        });
        await using (chat)
        {
            var surviving = chat.InputQueues.DefaultQueue;
            var notifications = 0;
            chat.InputQueues.Changed += (_, _) => notifications++;
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = removed.QueueId, ExpectedRevision = baseline.InputQueues.Revision, CommandId = Guid.NewGuid(),
            });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId, Status = status, Revision = 5,
                CurrentSnapshot = baseline.InputQueues with { Revision = 5 },
            });
            Assert.Equal(status, (await operation).Status);
            Assert.Equal(1, notifications);
            ApplyQueueFrame(chat, 3, new QueueChangedEvent
            {
                Revision = 2, Queues = [removed], RemovedQueueIds = [],
            });
            ApplyQueueFrame(chat, 4, new QueueChangedEvent
            {
                Revision = 5, Queues = [], RemovedQueueIds = [removed.QueueId],
            });
            Assert.Equal(5, chat.InputQueues.Snapshot.Revision);
            Assert.DoesNotContain(chat.InputQueues.Queues, queue => queue.Snapshot.QueueId == removed.QueueId);
            Assert.Same(surviving, chat.InputQueues.DefaultQueue);
            Assert.Equal(1, notifications);

            var waiting = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = "missing", ExpectedRevision = 5, CommandId = Guid.NewGuid(),
            });
            var waitingCommand = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 3, waitingCommand, new AgentInputQueueCommandResult
            {
                CommandId = waitingCommand.CommandId, Status = AgentInputQueueCommandStatus.Applied, Revision = 8,
            });
            var updated = surviving.Snapshot with { Name = "newer" };
            await transport.SendAsync(Frame(4, new QueueChangedEvent
            {
                Revision = 8, Queues = [updated], RemovedQueueIds = [],
            }));
            await waiting;
            Assert.Same(surviving, chat.InputQueues.DefaultQueue);
            Assert.Equal("newer", surviving.Snapshot.Name);
            Assert.Equal(2, notifications);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task InputQueues_DuplicateOrRegressiveStreamRevision_ThrowsDespiteSnapshotCoverage(long revision)
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            void Apply(long value) => ApplyQueueFrame(chat, 2,
                new QueueChangedEvent { Revision = value, Queues = [], RemovedQueueIds = [] });
            Apply(2);
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = "missing", ExpectedRevision = 2, CommandId = Guid.NewGuid(),
            });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId, Status = AgentInputQueueCommandStatus.Applied, Revision = 5,
                CurrentSnapshot = chat.InputQueues.Snapshot with { Revision = 5 },
            });
            await operation;
            Assert.IsType<RemoteAgentProtocolException>(Assert.Throws<TargetInvocationException>(() => Apply(revision)).InnerException);
            Assert.Equal(5, chat.InputQueues.Snapshot.Revision);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputQueues_FullSnapshot_ResetsCoverageAndScopesPendingCommandToRuntime(bool replacementEpoch)
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = "missing", ExpectedRevision = 1, CommandId = Guid.NewGuid(),
            });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            var baseline = AgentSessionProtocolCodecTests.Snapshot();
            var snapshotEvent = new SessionSnapshotEvent
            {
                Snapshot = baseline with { InputQueues = baseline.InputQueues with { Revision = replacementEpoch ? 0 : 4 } },
            };
            if (replacementEpoch)
            {
                // Isolate projection fencing from the client's transport authority checks.
                var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    new RuntimeEpoch { Value = Guid.NewGuid() }, 1, Guid.NewGuid(), snapshotEvent);
                await (Task)typeof(RemoteAgentChat).GetMethod("QueueForeground", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(chat, [(Action)(() => typeof(RemoteAgentChat).GetMethod("ApplyFrame",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chat, [frame]))])!;
            }
            else
            {
                var resynced = Event(chat, nameof(chat.InformationChanged));
                await transport.SendAsync(Frame(2, snapshotEvent));
                await transport.SendAsync(Frame(3, new AgentInformationChangedEvent { Information = baseline.Information }));
                await resynced;
            }
            await CompleteAsync(transport, replacementEpoch ? 2 : 4, command, new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId, Status = AgentInputQueueCommandStatus.Applied, Revision = 9,
                CurrentSnapshot = baseline.InputQueues with { Revision = 9 },
            });
            await operation;
            Assert.Equal(replacementEpoch ? 0 : 9, chat.InputQueues.Snapshot.Revision);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.InputQueues.Changed += (_, _) => changed.TrySetResult();
            await transport.SendAsync(Frame(replacementEpoch ? 3 : 5,
                new QueueChangedEvent { Revision = replacementEpoch ? 1 : 10, Queues = [], RemovedQueueIds = [] }));
            await changed.Task;
            Assert.Equal(replacementEpoch ? 1 : 10, chat.InputQueues.Snapshot.Revision);
        }
    }

    [Theory]
    [InlineData("removed-duplicate")]
    [InlineData("removed-empty")]
    [InlineData("queue-duplicate")]
    [InlineData("queue-invalid")]
    public async Task InputQueues_MalformedCoveredDelta_StillRejectsFrame(string kind)
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = "missing", ExpectedRevision = 1, CommandId = Guid.NewGuid(),
            });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId, Status = AgentInputQueueCommandStatus.Applied, Revision = 5,
                CurrentSnapshot = chat.InputQueues.Snapshot with { Revision = 5 },
            });
            await operation;
            var queue = chat.InputQueues.DefaultQueue.Snapshot;
            var malformed = new QueueChangedEvent
            {
                Revision = 2,
                Queues = kind switch
                {
                    "queue-duplicate" => [queue, queue],
                    "queue-invalid" => [queue with { Name = "" }],
                    _ => [],
                },
                RemovedQueueIds = kind switch
                {
                    "removed-duplicate" => ["gone", "gone"],
                    "removed-empty" => [""],
                    _ => [],
                },
            };
            var error = Assert.Throws<TargetInvocationException>(() => ApplyQueueFrame(chat, 3, malformed)).InnerException;
            if (kind.StartsWith("queue-", StringComparison.Ordinal))
                Assert.IsType<ArgumentException>(error);
            else
                Assert.IsType<RemoteAgentProtocolException>(error);
            ApplyQueueFrame(chat, 4, new QueueChangedEvent { Revision = 2, Queues = [], RemovedQueueIds = [] });
            Assert.Equal(5, chat.InputQueues.Snapshot.Revision);
        }
    }

    private static void ApplyQueueFrame(RemoteAgentChat chat, long sequence, QueueChangedEvent delta)
        => typeof(RemoteAgentChat).GetMethod("ApplyFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(chat, [AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                AgentSessionProtocolCodecTests.Epoch(), sequence, Guid.NewGuid(), delta)]);
}

using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Fact]
    public void AgentChatModal_InvalidIdentityTitleOrBody_RejectsInitialization()
    {
        static AgentChatModal Create(string id, string owner, string title, string body) => new()
        {
            Id = id, OwnerAgentId = owner, Title = title, Body = body,
            Content = new FreeformModalContent { IsRequired = false },
        };
        Assert.Throws<ArgumentException>(() => Create("", "owner", "title", "body"));
        Assert.Throws<ArgumentException>(() => Create("id", " ", "title", "body"));
        Assert.Throws<ArgumentException>(() => Create("id", "owner", "", "body"));
        Assert.Throws<ArgumentException>(() => Create("id", "owner", "title", "\t"));
    }

    [Fact]
    public void MultipleChoiceModalContent_Options_AreClonedOnInitialization()
    {
        var document = JsonDocument.Parse("""["one","two"]""");
        var content = new MultipleChoiceModalContent
        {
            Options = [document.RootElement[0], document.RootElement[1]], AllowsMultiple = true,
        };
        document.Dispose();
        Assert.Equal(["one", "two"], content.Options.Select(o => o.GetString()));
    }

    [Fact]
    public void FreeformModalContent_ValidSettings_RoundTrips()
    {
        var value = new FreeformModalContent { Placeholder = "Answer", IsRequired = true };
        var copy = JsonSerializer.Deserialize<AgentChatModalContent>(
            JsonSerializer.Serialize<AgentChatModalContent>(value, AgentSessionProtocolCodec.Options),
            AgentSessionProtocolCodec.Options);
        Assert.Equal(value, copy);
    }

    [Fact]
    public void MultipleChoiceModalContent_DuplicateOrEmptyOptions_RejectsInitialization()
    {
        Assert.Throws<ArgumentException>(() => new MultipleChoiceModalContent { Options = [], AllowsMultiple = false });
        var option = JsonDocument.Parse("\"same\"").RootElement.Clone();
        Assert.Throws<ArgumentException>(() => new MultipleChoiceModalContent { Options = [option, option], AllowsMultiple = false });
    }

    [Fact]
    public void ApprovalModalContent_BlankLabels_RejectsInitialization()
    {
        Assert.Throws<ArgumentException>(() => new ApprovalModalContent { ApproveLabel = "", RejectLabel = "No" });
        Assert.Throws<ArgumentException>(() => new ApprovalModalContent { ApproveLabel = "Yes", RejectLabel = " " });
    }

    [Fact]
    public async Task AttachAsync_InvalidSnapshot_ThrowsProtocolException()
    {
        var transport = new TestTransport();
        var client = new RemoteAgentSessionClient(transport);
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client, OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        var invalid = JsonNode.Parse(Frame(
            1, new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }).GetRawText())!;
        invalid["payload"]!["snapshot"]!["usage"]!["total-input-token-count"] = -1;
        await transport.SendAsync(JsonSerializer.SerializeToElement(invalid));
        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => attaching);
        Assert.True(transport.ChannelDisposed);
    }

    [Fact]
    public async Task Reconnect_UnexpectedLoss_RetriesWithinGraceAndKeepsProxyEpoch()
    {
        var transport = new ReconnectingChatTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await transport.SendAsync(0, Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await using var chat = await attaching;
        transport.Complete(0);
        await transport.SecondOpen;
        var usageApplied = Event(chat, nameof(chat.UsageChanged));
        await transport.SendAsync(1, Frame(2, new UsageChangedEvent
        {
            Usage = new Usage { TotalInputTokenCount = 99 },
        }));
        await usageApplied;

        var replay = transport.Opens[1].GetProperty("replay-cursor");
        Assert.Equal(AgentSessionProtocolCodecTests.Epoch().Value, replay.GetProperty("epoch").GetProperty("value").GetGuid());
        Assert.Equal(1, replay.GetProperty("sequence").GetInt64());
        Assert.Equal(99, chat.Usage.TotalInputTokenCount);
    }

    [Fact]
    public async Task ProxyGetters_AfterOrderedFrames_ReturnMirroredState()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var historyItem = new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("history")],
            };
            var runningItem = historyItem with { Contents = [new TextContent("streaming")] };
            var updatedItem = historyItem with { Contents = [new TextContent("updated")] };
            var toolSnapshot = new AgentChatToolItem("old", "Old", "Old", "", "function", false, []);
            var toolChanged = new AgentChatToolItem("new", "New", "New", "", "function", true, []);
            static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
            static JsonElement Subagent(string id) => Json(new
            {
                AgentId = id,
                DisplayName = id,
                Description = id,
                Name = id,
                CompletionState = AgentChatCompletionState.Running,
                LastUpdatedAt = DateTime.UnixEpoch,
                SubAgents = Array.Empty<object>(),
            });

            var usageApplied = Event(chat, nameof(chat.UsageChanged));
            await transport.SendAsync(Frame(2, new UsageChangedEvent { Usage = new Usage { TotalOutputTokenCount = 11 } }));
            await usageApplied;
            Assert.Equal(11, chat.Usage.TotalOutputTokenCount);

            var informationApplied = Event(chat, nameof(chat.InformationChanged));
            await transport.SendAsync(Frame(3, new AgentInformationChangedEvent
            {
                Information = AgentSessionProtocolCodecTests.Snapshot().Information with { DisplayName = "Changed" },
            }));
            await informationApplied;
            Assert.Equal("Changed", chat.Information.DisplayName);

            await transport.SendAsync(Frame(4, new HistoryAppendedEvent { Item = Json(historyItem) }));
            await transport.SendAsync(Frame(5, new StreamingStartedEvent { RunId = "run", Item = Json(runningItem) }));
            await transport.SendAsync(Frame(6, new StreamingUpdatedEvent
            {
                RunId = "run",
                Update = Json(new[] { updatedItem }),
            }));
            await transport.SendAsync(Frame(7, new BusyChangedEvent { IsBusy = true }));
            await transport.SendAsync(Frame(8, new ToolsSnapshotEvent { Tools = [Json(toolSnapshot)] }));
            await transport.SendAsync(Frame(9, new ToolsChangedEvent { Tools = [Json(toolChanged)] }));
            await transport.SendAsync(Frame(10, new SubagentsSnapshotEvent { Subagents = [Subagent("old-child")] }));
            await transport.SendAsync(Frame(11, new SubagentsChangedEvent { Subagents = [Subagent("new-child")] }));
            await transport.SendAsync(Frame(12, new ModalRaisedEvent { Modal = Modal() }));
            await transport.SendAsync(Frame(13, new ModalUpdatedEvent
            {
                Modal = Modal() with { Title = "Updated question" },
            }));
            await transport.SendAsync(Frame(14, new ModalDismissedEvent { ModalId = "modal" }));
            var allApplied = Event(chat, nameof(chat.InformationChanged));
            await transport.SendAsync(Frame(15, new AgentInformationChangedEvent
            {
                Information = AgentSessionProtocolCodecTests.Snapshot().Information with { DisplayName = "Sentinel" },
            }));
            await allApplied;

            Assert.Equal("history", Assert.IsType<TextContent>(chat.History.Single().Contents.Single()).Text);
            Assert.Equal("updated", Assert.IsType<TextContent>(chat.RunningItems.Single().Items.Single().Contents.Single()).Text);
            Assert.True(chat.IsBusy);
            Assert.Equal(("new", true), (chat.GetToolSnapshot().Single().Id, chat.GetToolSnapshot().Single().IsEnabled));
            Assert.Equal("new-child", chat.SubAgents.Single().AgentId);
            Assert.Empty(chat.Modals);
        }
    }

    [Fact]
    public async Task OrderedFrames_ConcurrentScheduler_AppliesStateThenNotifiesInSequence()
    {
        var transport = new TestTransport();
        var client = new RemoteAgentSessionClient(transport);
        var scheduler = new ManuallyReversedTaskScheduler();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client,
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = scheduler,
        });
        await transport.SendAsync(Frame(1, new SessionSnapshotEvent
        { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await scheduler.Queued;
        scheduler.RunNewest();
        await using var chat = await attaching;

        var clientReceivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, frame) =>
        {
            if (frame.Sequence == 3) clientReceivedBoth.TrySetResult();
        };
        await transport.SendAsync(Frame(2, new BusyChangedEvent { IsBusy = true }));
        await transport.SendAsync(Frame(3, new BusyChangedEvent { IsBusy = false }));
        await clientReceivedBoth.Task;

        scheduler.RunNewest();
        scheduler.RunNewest();
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task UsageChanged_OrderedFrame_AtomicallyReplacesUsage()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var observed = new TaskCompletionSource<Usage>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.UsageChanged += (_, _) => observed.TrySetResult(chat.Usage);
            var replacement = new Usage { TotalInputTokenCount = 20, TotalOutputTokenCount = 10, TotalSessionCostUsd = 1.5 };
            await transport.SendAsync(Frame(2, new UsageChangedEvent { Usage = replacement }));
            Assert.Equal(replacement, await observed.Task);
        }
    }

    [Fact]
    public async Task InformationChanged_OrderedFrame_AtomicallyReplacesInformation()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var observed = new TaskCompletionSource<AgentInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.InformationChanged += (_, _) => observed.TrySetResult(chat.Information);
            var replacement = AgentSessionProtocolCodecTests.Snapshot().Information with
            { DisplayName = "Replacement", CurrentModelId = "new-model" };
            await transport.SendAsync(Frame(2, new AgentInformationChangedEvent { Information = replacement }));
            var value = await observed.Task;
            Assert.Equal(("Replacement", "new-model"), (value.DisplayName, value.CurrentModelId));
        }
    }

    [Fact]
    public async Task InputQueues_RejectedCommand_DoesNotMutateProjection()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var before = chat.InputQueues.Snapshot;
            var id = Guid.NewGuid();
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            { QueueId = "missing", ExpectedRevision = before.Revision, CommandId = id });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
            {
                CommandId = id, Status = AgentInputQueueCommandStatus.Rejected,
                ErrorCode = AgentInputQueueErrorCodes.UnknownQueue, Revision = before.Revision,
            });
            Assert.Equal(AgentInputQueueCommandStatus.Rejected, (await operation).Status);
            Assert.Equal(before, chat.InputQueues.Snapshot);
        }
    }

    [Fact]
    public async Task InputQueues_ConflictResult_RefreshesFromAuthoritativeSnapshot()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var id = Guid.NewGuid();
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            { QueueId = "missing", ExpectedRevision = 1, CommandId = id });
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            var current = AgentSessionProtocolCodecTests.Snapshot().InputQueues with { Revision = 5 };
            await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
            {
                CommandId = id, Status = AgentInputQueueCommandStatus.Conflict,
                ErrorCode = "conflict", Revision = 5, CurrentSnapshot = current,
            });
            Assert.Equal(AgentInputQueueCommandStatus.Conflict, (await operation).Status);
            Assert.Equal(5, chat.InputQueues.Snapshot.Revision);
        }
    }

    [Fact]
    public async Task InputQueues_OlderCommandSnapshot_DoesNotReplaceNewerQueueEvent()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            {
                QueueId = "missing",
                ExpectedRevision = 1,
                CommandId = Guid.NewGuid(),
            });
            var command = AgentSessionProtocolCodec.DeserializeCommand(
                await transport.Outgoing.ReadAsync());
            var queueChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.InputQueues.Changed += (_, _) =>
            {
                if (chat.InputQueues.Snapshot.Revision == 6)
                    queueChanged.TrySetResult();
            };
            await transport.SendAsync(Frame(2, new QueueChangedEvent
            {
                Revision = 6,
                Queues = [],
                RemovedQueueIds = [],
            }));
            await queueChanged.Task;
            var stale = AgentSessionProtocolCodecTests.Snapshot().InputQueues with { Revision = 5 };

            await CompleteAsync(transport, 3, command, new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId,
                Status = AgentInputQueueCommandStatus.Conflict,
                ErrorCode = "conflict",
                Revision = 5,
                CurrentSnapshot = stale,
            });
            await operation;

            Assert.Equal(6, chat.InputQueues.Snapshot.Revision);
        }
    }

    [Fact]
    public async Task InputQueues_CommandPending_DoesNotMutateProjection()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var before = chat.InputQueues.Snapshot;
            var operation = chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            { QueueId = "missing", ExpectedRevision = 1, CommandId = Guid.NewGuid() });
            _ = await transport.Outgoing.ReadAsync();
            Assert.False(operation.IsCompleted);
            Assert.Equal(before, chat.InputQueues.Snapshot);
        }
    }

    [Fact]
    public async Task SetToolEnabledAsync_RemoteTool_SerializesCommandAndAppliesAcknowledgedEvent()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.SetToolEnabledAsync("tool", true);
            var command = Assert.IsType<SetToolEnabledCommand>(
                AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync()));
            await CompleteAsync(transport, 2, command);
            Assert.False(operation.IsCompleted);
            var tool = new AgentChatToolItem("tool", "Tool", "Description", "", "function", true, []);
            await transport.SendAsync(Frame(3, new ToolsChangedEvent
            {
                Tools = [JsonSerializer.SerializeToElement(tool, AIJsonUtilities.DefaultOptions)],
            }));
            await operation;
            Assert.True(chat.GetToolSnapshot().Single().IsEnabled);
        }
    }

    [Fact]
    public async Task RespondToModalAsync_CurrentModal_SerializesResponseCommand()
    {
        var snapshot = AgentSessionProtocolCodecTests.Snapshot() with { Modals = [Modal()] };
        var (transport, chat) = await AttachAsync(snapshot);
        await using (chat)
        {
            var operation = chat.RespondToModalAsync("modal", JsonDocument.Parse("""{"value":"yes"}""").RootElement);
            var command = Assert.IsType<ModalResponseCommand>(
                AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync()));
            Assert.Equal("modal", command.ModalId);
            await CompleteAsync(transport, 2, command);
            await operation;
        }
    }

    [Fact]
    public async Task EnqueueHelpNote_RemoteProxy_AddsLocalDisplayOnlyNote()
        => await AssertLocalNoteAsync((chat, text) => chat.EnqueueHelpNote(text), AgentChatHistoryItem.HelpChatRole);

    [Fact]
    public async Task EnqueueTransientDiagnostic_RemoteProxy_AddsLocalNonPersistedDiagnostic()
        => await AssertLocalNoteAsync((chat, text) => chat.EnqueueTransientDiagnostic(text), AgentChatHistoryItem.DiagnosticChatRole);

    [Fact]
    public async Task InterruptAsync_ConnectedProxy_AwaitsCommandCompletion()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.InterruptAsync(CancellationToken.None);
            var command = Assert.IsType<InterruptCommand>(
                AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync()));
            Assert.False(operation.IsCompleted);
            await CompleteAsync(transport, 2, command);
            await operation;
        }
    }

    [Fact]
    public async Task DetachAsync_RepeatedCall_SendsAtMostOneDetach()
    {
        var (transport, chat) = await AttachAsync();
        await chat.DetachAsync();
        await chat.DetachAsync();
        Assert.IsType<DetachCommand>(AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync()));
        Assert.False(transport.Outgoing.TryRead(out _));
    }

    [Fact]
    public async Task DetachAsync_LastViewer_DefaultPolicy_TerminatesRuntime()
        => await AssertDetachPolicyAsync(continueInBackground: false);

    [Fact]
    public async Task DetachAsync_LastViewer_BackgroundEnabled_PreservesRuntime()
        => await AssertDetachPolicyAsync(continueInBackground: true);

    [Fact]
    public void ShouldTerminateRuntime_NegativeViewerCount_RejectsInvalidState()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentSessionViewerReleasePolicy.ShouldTerminateRuntime(
                continueInBackground: false, remainingViewerCount: -1));

        Assert.Equal("remainingViewerCount", exception.ParamName);
    }

    [Fact]
    public async Task TerminateAsync_CurrentEpoch_WaitsForTerminalFrame()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var stateApplied = Event(chat, nameof(chat.InformationChanged));
            await transport.SendAsync(Frame(2, new StreamingStartedEvent
            {
                RunId = "active",
                Item = JsonSerializer.SerializeToElement(
                    new AgentChatHistoryItem
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new TextContent("running")],
                    },
                    AIJsonUtilities.DefaultOptions),
            }));
            await transport.SendAsync(Frame(3, new ModalRaisedEvent { Modal = Modal() }));
            await transport.SendAsync(Frame(4, new BusyChangedEvent { IsBusy = true }));
            await transport.SendAsync(Frame(5, new AgentInformationChangedEvent
            {
                Information = AgentSessionProtocolCodecTests.Snapshot().Information,
            }));
            await stateApplied;
            Assert.Single(chat.RunningItems);
            Assert.Single(chat.Modals);
            Assert.True(chat.IsBusy);

            var operation = chat.TerminateAsync();
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await CompleteAsync(transport, 6, command);
            Assert.False(operation.IsCompleted);
            await transport.SendAsync(Frame(7, new SessionTerminalEvent
            {
                Reason = "done", CompletionState = JsonDocument.Parse("""{"state":"completed"}""").RootElement.Clone(),
            }, command.CorrelationId));
            await operation;
            Assert.Empty(chat.RunningItems);
            Assert.Empty(chat.Modals);
            Assert.False(chat.IsBusy);
        }
    }

    [Fact]
    public async Task Snapshot_WithUnchangedModal_PreservesCollectionIdentity()
    {
        var snapshot = AgentSessionProtocolCodecTests.Snapshot() with { Modals = [Modal()] };
        var (transport, chat) = await AttachAsync(snapshot);
        await using (chat)
        {
            var original = Assert.Single(chat.Modals);
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)chat.Modals).CollectionChanged +=
                (_, args) => changes.Add(args.Action);
            var applied = Event(chat, nameof(chat.InformationChanged));

            await transport.SendAsync(Frame(2, new SessionSnapshotEvent { Snapshot = snapshot }));
            await transport.SendAsync(Frame(3, new AgentInformationChangedEvent
            {
                Information = snapshot.Information with { DisplayName = "Applied" },
            }));
            await applied;

            Assert.Same(original, Assert.Single(chat.Modals));
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, changes);
        }
    }

    [Fact]
    public async Task TerminateAsync_StaleEpoch_ThrowsRuntimeChanged()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var operation = chat.TerminateAsync();
            var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync());
            await transport.SendAsync(Frame(2, new OperationErrorEvent
            {
                Error = new RemoteAgentOperationError
                {
                    Code = "runtime-changed", Operation = "terminate-session", IsRetryable = false,
                    Message = "Runtime changed.", CorrelationId = command.CorrelationId,
                },
            }, command.CorrelationId));
            Assert.Equal("runtime-changed", (await Assert.ThrowsAsync<RemoteAgentSessionException>(() => operation)).Code);
        }
    }

    [Fact]
    public async Task DisposeAsync_ConnectedProxy_ReleasesViewerWithoutTerminateCommand()
    {
        var (transport, chat) = await AttachAsync();
        await chat.DisposeAsync();
        Assert.True(transport.ChannelDisposed);
        Assert.False(transport.Outgoing.TryRead(out _));
    }

    [Fact]
    public async Task DisposeAsync_QueuedForegroundFrame_AwaitsSchedulerOwnership()
    {
        var transport = new TestTransport();
        var scheduler = new ManuallyReversedTaskScheduler();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = scheduler,
        });
        await transport.SendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await scheduler.Queued;
        scheduler.RunNewest();
        var chat = await attaching;

        await transport.SendAsync(Frame(2, new BusyChangedEvent { IsBusy = true }));
        await scheduler.Queued;
        var disposing = chat.DisposeAsync().AsTask();

        Assert.False(disposing.IsCompleted);
        scheduler.RunNewest();
        await disposing;
        Assert.True(transport.ChannelDisposed);
    }

    private static async Task<(TestTransport Transport, RemoteAgentChat Chat)> AttachAsync(AgentSessionSnapshot snapshot)
    {
        var transport = new TestTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport), OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await transport.SendAsync(Frame(1, new SessionSnapshotEvent { Snapshot = snapshot }));
        return (transport, await attaching);
    }

    private static Task Event(RemoteAgentChat chat, string eventName)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => completion.TrySetResult();
        typeof(RemoteAgentChat).GetEvent(eventName)!.AddEventHandler(chat, handler);
        return completion.Task;
    }

    private static AgentChatModal Modal() => new()
    {
        Id = "modal", OwnerAgentId = "agent", Title = "Question", Body = "Choose",
        Content = new FreeformModalContent { IsRequired = true },
    };

    private static ValueTask CompleteAsync(
        TestTransport transport, long sequence, AgentSessionCommand command, object? result = null)
        => transport.SendAsync(Frame(sequence, new CommandCompletedEvent
        {
            CommandId = command.CommandId,
            Result = result is null ? null : JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options),
        }, command.CorrelationId));

    private static async Task AssertLocalNoteAsync(Action<RemoteAgentChat, string> enqueue, ChatRole role)
    {
        var (_, chat) = await AttachAsync();
        await using (chat)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)chat.History).CollectionChanged += (_, _) => changed.TrySetResult();
            enqueue(chat, "local");
            await changed.Task;
            Assert.Equal(role, chat.History.Single().Role);
        }
    }

    private static async Task AssertDetachPolicyAsync(bool continueInBackground)
    {
        var snapshot = AgentSessionProtocolCodecTests.Snapshot() with
        { ContinueInBackground = continueInBackground, ViewerCount = 1 };
        var (transport, chat) = await AttachAsync(snapshot);
        Assert.Equal(continueInBackground, chat.ContinueInBackground);
        await chat.DetachAsync();
        var command = Assert.IsType<DetachCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.Outgoing.ReadAsync()));
        Assert.Equal("detach", command.Type);
        Assert.Equal(!continueInBackground,
            AgentSessionViewerReleasePolicy.ShouldTerminateRuntime(continueInBackground, remainingViewerCount: 0));
        Assert.False(AgentSessionViewerReleasePolicy.ShouldTerminateRuntime(
            continueInBackground, remainingViewerCount: 1));
    }

    private sealed class ManuallyReversedTaskScheduler : TaskScheduler
    {
        private readonly object sync = new();
        private readonly List<Task> queued = [];
        private TaskCompletionSource queuedSignal = NewSignal();

        public Task Queued
        {
            get
            {
                lock (this.sync)
                    return this.queued.Count > 0 ? Task.CompletedTask : this.queuedSignal.Task;
            }
        }

        protected override void QueueTask(Task task)
        {
            lock (this.sync)
            {
                this.queued.Add(task);
                this.queuedSignal.TrySetResult();
            }
        }

        public void RunNewest()
        {
            Task task;
            lock (this.sync)
            {
                task = this.queued[^1];
                this.queued.RemoveAt(this.queued.Count - 1);
                if (this.queued.Count == 0) this.queuedSignal = NewSignal();
            }
            Assert.True(this.TryExecuteTask(task));
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (this.sync) return this.queued.ToArray();
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReconnectingChatTransport : ITransport
    {
        private readonly List<ReconnectingChatChannel> channels = [];
        private readonly TaskCompletionSource secondOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<JsonElement> Opens { get; } = [];
        public Task SecondOpen => this.secondOpen.Task;

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var channel = new ReconnectingChatChannel();
            this.channels.Add(channel);
            this.Opens.Add(request.Clone());
            if (this.channels.Count == 2) this.secondOpen.TrySetResult();
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask SendAsync(int index, JsonElement value) => this.channels[index].Incoming.Writer.WriteAsync(value);
        public void Complete(int index) => this.channels[index].Incoming.Writer.TryComplete();
    }

    private sealed class ReconnectingChatChannel : IMessageChannel
    {
        public Channel<JsonElement> Outgoing { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> Incoming { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.Outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.Incoming.Reader;
        public ValueTask DisposeAsync()
        {
            this.Outgoing.Writer.TryComplete();
            this.Incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

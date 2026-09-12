using System.Linq;
using System.Collections.ObjectModel;
using System.Reflection;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// #1485 retry: contract tests for the AgentViewModel common-surface migration and the
/// modal/interrupt/dismiss/dispose forwarding invariants. Uses the reflection-based
/// <c>CreateChat</c> helper reused from the sibling status-line tests so we exercise the real
/// concrete <see cref="AgentChat"/> without needing a full transport stack.
/// </summary>
public sealed class AgentViewModelRetryTests
{
    private static AgentDefinition MakeDefinition() =>
        AgentDefinitionLoader.LoadAgentFromJson(
            """
            { "kind": "prompt", "name": "test-agent",
              "model": { "id": "gpt-4o", "provider": "github-models", "apiType": "Echo" },
              "tools": [] }
            """);

    private readonly TaskScheduler foregroundScheduler = new SynchronousTaskScheduler();

    private AgentChat CreateChat(AgentDefinition? def, AgentServices? agentServices = null)
    {
        var reqType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")!;
        var request = Activator.CreateInstance(reqType)!;
        reqType.GetProperty("AgentDefinition")!.SetValue(request, def);
        reqType.GetProperty("ConfiguredStore")!.SetValue(request, new InMemoryAgentPersistenceStore());
        reqType.GetProperty("AgentServices")!.SetValue(request, agentServices);
        reqType.GetProperty("ClientOverride")!.SetValue(request, agentServices?.ChatClientOverride);
        // #1485 retry: synchronous foreground scheduler makes PublishModal / RespondToModalAsync /
        // PublishModalDismiss execute inline so tests do not depend on Task.Yield polling loops.
        reqType.GetProperty("ForegroundScheduler")!.SetValue(request, this.foregroundScheduler);
        var ctor = typeof(AgentChat).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { reqType }, null)!;
        var chat = (AgentChat)ctor.Invoke(new[] { request });
        typeof(AgentChat).GetField("agentDefinition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chat, def);
        if (def is not null)
        {
            chat.PublishInformation();
        }
        return chat;
    }

    private AgentViewModel CreateViewModel(IAgentChat chat, ObservableLoggerFactory loggerFactory)
        => new(new AgentViewModelOptions
        {
            AgentChat = chat,
            DisplayName = "display",
            Description = "description",
            LoggerFactory = loggerFactory,
            ForegroundScheduler = TaskScheduler.Default,
        });

    // #1226 pattern: inline foreground scheduler so publish/respond/dismiss run inline.
    private sealed class SynchronousTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();
        protected override void QueueTask(Task task) => this.TryExecuteTask(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            => this.TryExecuteTask(task);
    }

    [Fact]
    public async Task AgentViewModel_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var localChat = CreateChat(MakeDefinition());
        await using var remoteChat = new RemoteAgentChatProxy(localChat);
        await using var localVm = this.CreateViewModel(localChat, loggerFactory);
        await using var remoteVm = this.CreateViewModel(remoteChat, loggerFactory);

        Assert.IsAssignableFrom<IAgentChat>(localVm.AgentChat);
        Assert.IsAssignableFrom<IAgentChat>(remoteVm.AgentChat);
        Assert.Same(localChat, localVm.AgentChat);
        Assert.Same(remoteChat, remoteVm.AgentChat);
        Assert.NotNull(localVm.InputQueue);
        Assert.NotNull(remoteVm.InputQueue);
        Assert.Equal(localVm.InputQueue!.InputQueues.Select(q => q.QueueId), remoteVm.InputQueue!.InputQueues.Select(q => q.QueueId));
    }

    [Fact]
    public async Task RunningAgentChatLease_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var localChat = CreateChat(MakeDefinition());
        var lease = (RunningAgentChatLease)Activator.CreateInstance(
            typeof(RunningAgentChatLease),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new AgentSessionId("lease-1"),
                localChat,
                (Func<ValueTask>)(() => ValueTask.CompletedTask),
                localChat,
                null,
            ],
            culture: null)!;
        await using var remoteChat = new RemoteAgentChatProxy(localChat);
        await using var vm = this.CreateViewModel(remoteChat, loggerFactory);

        Assert.IsAssignableFrom<IAgentChat>(lease.AgentChat);
        Assert.Same(localChat, lease.AgentChat);
        Assert.NotNull(vm.InputQueue);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Constructor_RemoteChat_UsesCommonSurfaceWithoutConcreteCast()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var localChat = CreateChat(MakeDefinition());
        await using var remoteChat = new RemoteAgentChatProxy(localChat);
        await using var vm = this.CreateViewModel(remoteChat, loggerFactory);

        Assert.Same(remoteChat, vm.AgentChat);
        Assert.NotNull(vm.InputQueue);
        Assert.Equal(remoteChat.Information.AgentSessionId, vm.AgentSessionId);
        Assert.Equal(vm.InputQueue!.InputQueues.Select(q => q.QueueId), remoteChat.InputQueues.Snapshot.Queues.Where(q => !q.IsImmediate).Select(q => q.QueueId));
    }

    [Fact]
    public async Task AgentViewModelOptions_NamedInitializer_PreservesRequiredValuesAndParentDefault()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        var opts = new AgentViewModelOptions
        {
            AgentChat = chat,
            DisplayName = "d",
            Description = "e",
            LoggerFactory = loggerFactory,
            ForegroundScheduler = TaskScheduler.Default,
        };
        Assert.Same(chat, opts.AgentChat);
        Assert.Equal("d", opts.DisplayName);
        Assert.Equal("e", opts.Description);
        Assert.Null(opts.ParentAgentViewModel);
    }

    [Fact]
    public async Task Constructor_WrongForegroundContext_Throws()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        var original = SynchronizationContext.Current;
        var current = new SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(current);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new AgentViewModel(
                new AgentViewModelOptions
                {
                    AgentChat = chat,
                    DisplayName = "d",
                    Description = "e",
                    LoggerFactory = loggerFactory,
                    ForegroundScheduler = new SynchronizationContextTaskScheduler(new SynchronizationContext()),
                }));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    [Fact]
    public async Task RespondToModalAsync_UnknownModal_ThrowsArgumentException()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        await Assert.ThrowsAsync<ArgumentException>(
            () => vm.RespondToModalAsync("nope", default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RespondToModalAsync_CurrentModal_SendsResponseAndKeepsInputGatedUntilDismissed()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        var modal = new AgentChatModal
        {
            Id = "m",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        chat.PublishModal(modal);
        Assert.Single(vm.Modals);
        Assert.True(vm.IsInputGated);
        var response = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone();
        var modalViewModel = Assert.Single(vm.Modals);
        var respondTask = modalViewModel.RespondAsync(response, TestContext.Current.CancellationToken);
        Assert.False(respondTask.IsCompleted);
        Assert.Single(vm.Modals);
        vm.DismissModal("m");
        await respondTask;
        Assert.Empty(vm.Modals);
        Assert.False(vm.IsInputGated);
    }

    [Fact]
    public async Task RespondToModalAsync_Cancelled_DoesNotDismissModal()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        var modal = new AgentChatModal
        {
            Id = "m",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        chat.PublishModal(modal);
        Assert.Single(chat.Modals);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var response = System.Text.Json.JsonDocument.Parse("true").RootElement.Clone();
        var modalViewModel = Assert.Single(vm.Modals);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => modalViewModel.RespondAsync(response, cts.Token));
        Assert.Single(vm.Modals);
        Assert.True(vm.IsInputGated);
    }

    [Fact]
    public async Task ModalEvent_DescendantModal_UpdatesRootAggregateOnly()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var rootChat = CreateChat(MakeDefinition());
        await using var childChat = CreateChat(MakeDefinition());
        await using var root = this.CreateViewModel(rootChat, loggerFactory);
        await ((ISubAgentTable)rootChat).Add(childChat);
        var modal = new AgentChatModal
        {
            Id = "child-modal",
            OwnerAgentId = childChat.AgentId,
            Title = "Child",
            Body = "Needs input",
            Content = new FreeformModalContent { IsRequired = true },
        };

        childChat.PublishModal(modal);

        Assert.Empty(root.Modals);
        Assert.True(root.HasModalsNeedingInput);
        Assert.Empty(rootChat.Modals);
    }

    [Fact]
    public async Task RemovedSubAgent_WithModal_ClearsRootAggregate()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var rootChat = CreateChat(MakeDefinition());
        await using var childChat = CreateChat(MakeDefinition());
        await using var root = this.CreateViewModel(rootChat, loggerFactory);
        var child = await ((ISubAgentTable)rootChat).Add(childChat);
        childChat.PublishModal(new AgentChatModal
        {
            Id = "removed-child-modal",
            OwnerAgentId = childChat.AgentId,
            Title = "Child",
            Body = "Needs input",
            Content = new FreeformModalContent { IsRequired = true },
        });
        Assert.True(root.HasModalsNeedingInput);

        var field = typeof(AgentChat).GetField(
            "subAgentItems",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var items = Assert.IsType<ObservableCollection<IRunningSubAgent>>(field!.GetValue(rootChat));
        items.Remove(child);

        Assert.False(root.HasModalsNeedingInput);
    }

    [Fact]
    public async Task AgentSessionModalViewModel_RespondAsync_InvalidOption_RejectsBeforeTransport()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = CreateChat(MakeDefinition());
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        chat.PublishModal(new AgentChatModal
        {
            Id = "choice",
            OwnerAgentId = "owner",
            Title = "Choose",
            Body = "Choose one",
            Content = new MultipleChoiceModalContent
            {
                Options =
                [
                    System.Text.Json.JsonDocument.Parse("\"yes\"").RootElement.Clone(),
                ],
                AllowsMultiple = false,
            },
        });

        var modal = Assert.Single(vm.Modals);
        var invalid = System.Text.Json.JsonDocument.Parse("\"no\"").RootElement.Clone();

        await Assert.ThrowsAsync<ArgumentException>(() => modal.RespondAsync(invalid, TestContext.Current.CancellationToken));
        Assert.Single(vm.Modals);
        Assert.True(vm.IsInputGated);
    }

    [Fact]
    public async Task DisposeAsync_RemoteChat_UnsubscribesBeforeDetaching()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var local = CreateChat(MakeDefinition());
        var remote = new RemoteAgentChatProxy(local);
        var vm = this.CreateViewModel(remote, loggerFactory);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        await vm.DisposeAsync();
        var before = notifications;
        local.SetAgentSessionId("after-detach");

        Assert.Equal(before, notifications);
    }

    [Fact]
    public async Task InterruptCommand_RemoteChat_InvokesCommonInterrupt()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("blocked")],
        }, isReady: false);
        stream.Complete(isReady: false);
        await using var local = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = MakeDefinition(),
                AgentServices = new AgentServices { ChatClientOverride = client },
            });
        await using var remote = new RemoteAgentChatProxy(local);
        await using var vm = this.CreateViewModel(remote, loggerFactory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)local.RunningItems).CollectionChanged += (_, _) =>
        {
            if (local.RunningItems.Count > 0)
            {
                started.TrySetResult();
            }
        };
        local.TurnCompleted += (_, _) => completed.TrySetResult();
        local.EnqueueUserMessage("start");
        await started.Task.WaitAsync(CancellationToken.None);

        var interruptCommand = Assert.IsType<AsyncRelayCommand>(vm.InterruptCommand);
        interruptCommand.Execute(null);
        await interruptCommand.LastExecutionTask!;
        await completed.Task.WaitAsync(CancellationToken.None);
        Assert.Empty(local.RunningItems);

        var noteAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)local.History).CollectionChanged += (_, _) =>
        {
            if (local.History.Any(item => item.Contents.OfType<Microsoft.Extensions.AI.TextContent>()
                .Any(content => content.Text == "usable-after-remote-interrupt")))
            {
                noteAdded.TrySetResult();
            }
        };
        local.EnqueueSystemNote("usable-after-remote-interrupt");
        await noteAdded.Task.WaitAsync(CancellationToken.None);
        Assert.Contains(local.History,
            item => item.Contents.OfType<Microsoft.Extensions.AI.TextContent>()
                .Any(content => content.Text == "usable-after-remote-interrupt"));
    }

    [Fact]
    public async Task InterruptCommand_RepeatedShortcut_IsSingleFlightAndRetriesAfterSuccess()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var chat = new StubAgentChat(
            new DeferredQueues(),
            MakeDefinition(),
            _ =>
            {
                Interlocked.Increment(ref calls);
                return firstCompletion.Task;
            });
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        var command = Assert.IsType<AsyncRelayCommand>(vm.InterruptCommand);
        var availabilityChanges = new List<bool>();
        command.CanExecuteChanged += (_, _) => availabilityChanges.Add(command.CanExecute(null));

        command.Execute(null);
        var firstExecution = command.LastExecutionTask;
        command.Execute(null);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Same(firstExecution, command.LastExecutionTask);
        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(null));
        Assert.Equal([false], availabilityChanges);

        firstCompletion.SetResult();
        await firstExecution!;

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
        Assert.Equal([false, true], availabilityChanges);

        command.Execute(null);
        await command.LastExecutionTask!;
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task InterruptCommand_FailureAndCancellation_PropagateAndReleaseSingleFlightGate()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        var completions = new Queue<TaskCompletionSource>();
        var chat = new StubAgentChat(
            new DeferredQueues(),
            MakeDefinition(),
            _ => completions.Dequeue().Task);
        await using var vm = this.CreateViewModel(chat, loggerFactory);
        var command = Assert.IsType<AsyncRelayCommand>(vm.InterruptCommand);

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completions.Enqueue(failed);
        command.Execute(null);
        failed.SetException(new InvalidOperationException("owner rejected"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await command.LastExecutionTask!);
        Assert.Equal("owner rejected", failure.Message);
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));

        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completions.Enqueue(canceled);
        command.Execute(null);
        canceled.SetCanceled(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await command.LastExecutionTask!);
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task InterruptCommand_PendingDuringViewDisposal_ReleasesGateOnCompletion()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new StubAgentChat(new DeferredQueues(), MakeDefinition(), _ => completion.Task);
        var vm = this.CreateViewModel(chat, loggerFactory);
        var command = Assert.IsType<AsyncRelayCommand>(vm.InterruptCommand);

        command.Execute(null);
        var execution = command.LastExecutionTask!;
        await vm.DisposeAsync();

        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(null));

        completion.SetResult();
        await execution;
        Assert.False(command.IsExecuting);
        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task ConfigureSlashCommands_RemoteChat_RegistersOnlyCommonHandlers()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var local = CreateChat(MakeDefinition());
        local.SlashCommands.Register(new FakeSlashCommandHandler("engine-only"));
        await using var remote = new RemoteAgentChatProxy(local);
        await using var vm = this.CreateViewModel(remote, loggerFactory);

        vm.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = remote });

        var commands = remote.SlashCommands.Commands.Select(command => command.Name).Order().ToArray();
        Assert.DoesNotContain("engine-only", commands);
        Assert.Equal(["auto-resume", "clone", "help", "input-help", "reasoning", "rename", "restart", "title"], commands);
    }

    [Fact]
    public async Task CommandPending_RemoteQueue_DoesNotMutateProjectionOptimistically()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var before = remote.InputQueues.Snapshot;
        var command = source.NewEnqueueRequest("pending");
        var pending = remote.InputQueues.EnqueueAsync(command, TestContext.Current.CancellationToken);

        Assert.False(pending.IsCompleted);
        Assert.Equal(before, remote.InputQueues.Snapshot);
        Assert.Empty(remote.InputQueues.DefaultQueue.Snapshot.Items);
        source.CompleteApplied(command);
        var result = await pending;
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
    }

    [Fact]
    public async Task InputQueueViewModel_CommandPending_RemoteQueue_DoesNotMutateProjectionOptimistically()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = remote,
            HiddenBuiltInQueueId = "not-the-default-queue",
            ForegroundScheduler = this.foregroundScheduler,
        });

        var pending = viewModel.AppendToQueueAsync(
            "queue-1",
            [new TextContent("pending")],
            TestContext.Current.CancellationToken);

        Assert.False(pending.IsCompleted);
        Assert.Empty(Assert.Single(viewModel.Queues).Items);

        source.CompletePending();
        await pending;

        Assert.Single(Assert.Single(viewModel.Queues).Items);
    }

    [Fact]
    public async Task InputQueueViewModel_CommandConflict_ReplacesProjectionFromAuthoritativeSnapshot()
    {
        var source = new DeferredQueues { ConflictNextEnqueue = true };
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = remote,
            HiddenBuiltInQueueId = "not-the-default-queue",
            ForegroundScheduler = this.foregroundScheduler,
        });

        var result = await viewModel.AppendToQueueAsync(
            "queue-1",
            [new TextContent("stale")],
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, result.Status);
        Assert.Empty(Assert.Single(viewModel.Queues).Items);
        Assert.Equal(source.Snapshot.Revision, remote.InputQueues.Snapshot.Revision);
    }

    [Fact]
    public async Task InputQueueViewModel_Dispose_UnsubscribesChangedWithoutDisposingOwnedAggregate()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = remote,
            HiddenBuiltInQueueId = "not-the-default-queue",
            ForegroundScheduler = this.foregroundScheduler,
        });

        viewModel.Dispose();
        source.RaiseChanged();

        Assert.NotNull(remote.InputQueues);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CommandConflict_StaleRevision_RefreshesFromAuthoritativeSnapshot()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var stale = source.NewEnqueueRequest("stale");
        stale = stale with { ExpectedRevision = stale.ExpectedRevision - 1 };

        var result = await remote.InputQueues.EnqueueAsync(stale, TestContext.Current.CancellationToken);

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, result.Status);
        Assert.Equal(source.Snapshot.Revision, remote.InputQueues.Snapshot.Revision);
        Assert.Equal(source.Snapshot.Queues.Single().QueueId, remote.InputQueues.Snapshot.Queues.Single().QueueId);
        Assert.Equal(source.Snapshot.Revision, result.CurrentSnapshot?.Revision);
    }

    [Fact]
    public async Task CommandApplied_AuthoritativeDeltaAppliedBeforeTaskCompletes()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var observedRevisions = new List<long>();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.InputQueues.Changed += (_, _) =>
        {
            observedRevisions.Add(remote.InputQueues.Snapshot.Revision);
            changed.TrySetResult();
        };
        var command = source.NewEnqueueRequest("applied");
        var pending = remote.InputQueues.EnqueueAsync(command, TestContext.Current.CancellationToken);

        source.PublishAppliedButDoNotComplete(command);
        await changed.Task.WaitAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Contains(remote.InputQueues.DefaultQueue.Snapshot.Items, item => item.ItemId == source.LastItemId);
        source.ReleaseCompletion();
        await pending;
        Assert.Equal(source.Snapshot.Revision, observedRevisions.Single());
    }

    [Fact]
    public async Task QueueComposer_AcknowledgedSubmission_RemovesOnlySubmittedAttachments()
    {
        var source = new DeferredQueues();
        await using var remote = new RemoteAgentChatProxy(new StubAgentChat(source, MakeDefinition()));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = remote,
            HiddenBuiltInQueueId = "not-the-default-queue",
        });
        var composer = viewModel.DefaultComposer;
        composer.AppendImageAttachment([1], "image/png", 1, 1, "submitted.png");
        composer.InputText += " message";

        var submission = composer.SubmitAsync(TestContext.Current.CancellationToken);
        composer.AppendImageAttachment([2], "image/png", 2, 2, "new.png");
        source.CompletePending();

        Assert.True(await submission);
        Assert.True(composer.HasAttachments);
        Assert.Single(composer.AttachmentPreviews);
        Assert.DoesNotContain("submitted.png", composer.InputText, StringComparison.Ordinal);
        Assert.Contains("new.png", composer.InputText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    [Fact]
    public async Task AgentChatSetter_LocalOrRemote_PreservesCommonChat()
    {
        await using var localChat = CreateChat(MakeDefinition());
        await using var remoteChat = new RemoteAgentChatProxy(localChat);
        var localContext = new SlashCommandContext { AgentChat = localChat };
        var remoteContext = new SlashCommandContext { AgentChat = remoteChat };

        Assert.Same(localChat, localContext.AgentChat);
        Assert.Same(remoteChat, remoteContext.AgentChat);
    }

    [Fact]
    public async Task AgentChatSetter_Null_RejectsInitialization()
    {
        Assert.Throws<ArgumentNullException>(() => new SlashCommandContext { AgentChat = null! });
        await Task.CompletedTask;
    }

    private sealed class FakeSlashCommandHandler(string name) : ISlashCommandHandler
    {
        public string Name => name;
        public string Description => name;
        public string? Usage => "/" + name;
        public string? LongDescription => name;
        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = name });
    }

#pragma warning disable CS0067
    private sealed class StubAgentChat(
        DeferredQueues inputQueues,
        AgentDefinition definition,
        Func<CancellationToken, Task>? interruptAsync = null) : IAgentChat
    {
        private readonly ReadOnlyObservableCollection<IRunningSubAgent> subAgents = new(new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>());
        private readonly ReadOnlyObservableCollection<AgentChatModal> modals = new(new System.Collections.ObjectModel.ObservableCollection<AgentChatModal>());

        public AgentInformation Information { get; private set; } = new()
        {
            AgentSessionId = "stub-session",
            AgentId = "stub-agent",
            Name = "stub-agent",
            DisplayName = "Stub Agent",
            Description = "Stub Agent",
            AcceptsUserInput = true,
            AgentDefinition = definition,
        };

        public Usage Usage => default;
        public bool IsBusy => false;
        public AgentChatHistoryCollection History { get; } = new();
        public Task HistoryPopulated => Task.CompletedTask;
        public AgentChatRunningItemCollection RunningItems { get; } = new();
        public IAgentInputQueues InputQueues { get; } = inputQueues;
        public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents => this.subAgents;
        public ReadOnlyObservableCollection<AgentChatModal> Modals => this.modals;
        public ISlashCommandRegistry SlashCommands { get; } = new SlashCommandRegistry();
        public event EventHandler? InformationChanged;
        public event EventHandler? ToolsChanged;
        public event EventHandler? UsageChanged;
        public event EventHandler<AgentChatHistoryItem>? TurnCompleted;
        public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => [];
        public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task RespondToModalAsync(string modalId, System.Text.Json.JsonElement response, CancellationToken ct = default) => Task.CompletedTask;
        public void EnqueueSystemNote(string text) { }
        public void EnqueueHelpNote(string text) { }
        public void EnqueueTransientDiagnostic(string text) { }
        public Task InterruptAsync(CancellationToken ct = default) =>
            interruptAsync?.Invoke(ct) ?? Task.CompletedTask;
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067

    private sealed class DeferredQueues : IAgentInputQueues
    {
        private readonly string queueId = "queue-1";
        private readonly DeferredQueue queue;
        private TaskCompletionSource<AgentInputQueueCommandResult>? pendingCompletion;
        private Guid pendingCommandId;
        private EnqueueAgentInputRequest? pendingRequest;
        private long revision;
        public string? LastItemId { get; private set; }
        public bool ConflictNextEnqueue { get; init; }

        public DeferredQueues()
        {
            this.Snapshot = this.CreateSnapshot([]);
            this.queue = new DeferredQueue(this.Snapshot.Queues.Single());
        }

        public AgentInputQueuesSnapshot Snapshot { get; private set; }
        public IReadOnlyList<IAgentInputQueue> Queues => [this.queue];
        public IAgentInputQueue DefaultQueue => this.queue;
        public IAgentInputQueue ImmediateQueue => this.queue;
        public event EventHandler? Changed;

        public EnqueueAgentInputRequest NewEnqueueRequest(string text) => new()
        {
            TargetQueueId = this.queueId,
            Messages = [new ChatMessage(ChatRole.User, text)],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = this.Snapshot.Revision,
        };

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) => throw new NotSupportedException();

        public Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default)
        {
            if (this.ConflictNextEnqueue)
            {
                return Task.FromResult(new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Conflict,
                    Revision = this.Snapshot.Revision,
                    CurrentSnapshot = this.Snapshot,
                });
            }
            if (request.ExpectedRevision != this.Snapshot.Revision)
            {
                return Task.FromResult(new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Conflict,
                    Revision = this.Snapshot.Revision,
                    CurrentSnapshot = this.Snapshot,
                });
            }

            this.pendingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.pendingCommandId = request.CommandId;
            this.pendingRequest = request;
            this.LastItemId = Guid.NewGuid().ToString("N");
            return this.pendingCompletion.Task;
        }

        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) => throw new NotSupportedException();

        public void PublishAppliedButDoNotComplete(EnqueueAgentInputRequest request)
        {
            this.Apply(request);
        }

        public void CompleteApplied(EnqueueAgentInputRequest request)
        {
            this.Apply(request);
            this.ReleaseCompletion();
        }

        public void CompletePending()
        {
            if (this.pendingRequest is not { } request)
            {
                throw new InvalidOperationException("No pending queue command.");
            }

            this.CompleteApplied(request);
        }

        public void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);

        public void ReleaseCompletion()
        {
            this.pendingCompletion?.TrySetResult(new AgentInputQueueCommandResult
            {
                CommandId = this.pendingCommandId,
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = this.Snapshot.Revision,
                ItemId = this.LastItemId,
                CurrentSnapshot = this.Snapshot,
            });
        }

        private void Apply(EnqueueAgentInputRequest request)
        {
            this.revision++;
            this.Snapshot = this.CreateSnapshot(
                [
                    new AgentInputItemSnapshot
                    {
                        ItemId = this.LastItemId!,
                        Messages = [.. request.Messages],
                    },
                ]);
            this.queue.Update(this.Snapshot.Queues.Single());
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        private AgentInputQueuesSnapshot CreateSnapshot(IEnumerable<AgentInputItemSnapshot> items) => new()
        {
            Revision = this.revision,
            Queues =
            [
                new AgentInputQueueSnapshot
                {
                    QueueId = this.queueId,
                    Name = "default",
                    IsDefault = true,
                    IsImmediate = false,
                    Immediacy = AgentInputQueueImmediacy.Queue,
                    Priority = 0,
                    Revision = this.revision,
                    Items = [.. items],
                },
            ],
        };

        private sealed class DeferredQueue(AgentInputQueueSnapshot snapshot) : IAgentInputQueue
        {
            public AgentInputQueueSnapshot Snapshot { get; private set; } = snapshot;
            public event EventHandler? Changed;
            public void Update(AgentInputQueueSnapshot snapshot)
            {
                this.Snapshot = snapshot;
                this.Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

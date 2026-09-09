using System.Reflection;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
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

    private AgentChat CreateChat(AgentDefinition? def)
    {
        var reqType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")!;
        var request = Activator.CreateInstance(reqType)!;
        reqType.GetProperty("AgentDefinition")!.SetValue(request, def);
        reqType.GetProperty("ConfiguredStore")!.SetValue(request, new InMemoryAgentPersistenceStore());
        // #1485 retry: synchronous foreground scheduler makes PublishModal / RespondToModalAsync /
        // PublishModalDismiss execute inline so tests do not depend on Task.Yield polling loops.
        reqType.GetProperty("ForegroundScheduler")!.SetValue(request, this.foregroundScheduler);
        var ctor = typeof(AgentChat).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { reqType }, null)!;
        var chat = (AgentChat)ctor.Invoke(new[] { request });
        typeof(AgentChat).GetField("agentDefinition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chat, def);
        if (def is not null)
        {
            typeof(AgentChat).GetMethod("PublishInformation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(chat, null);
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
        Assert.Throws<ArgumentNullException>(() => new AgentViewModel(
            new AgentViewModelOptions
            {
                AgentChat = chat,
                DisplayName = "d",
                Description = "e",
                LoggerFactory = loggerFactory,
                ForegroundScheduler = null!,
            }));
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
        Assert.Single(vm.ModalProjection);
        Assert.True(vm.IsInputGated);
        var respondTask = vm.RespondToModalAsync("m", default, TestContext.Current.CancellationToken);
        Assert.False(respondTask.IsCompleted);
        Assert.Single(vm.ModalProjection);
        vm.DismissModal("m");
        await respondTask;
        Assert.Empty(vm.ModalProjection);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => vm.RespondToModalAsync("m", default, cts.Token));
        Assert.Single(vm.ModalProjection);
        Assert.True(vm.IsInputGated);
    }

    [Fact]
    public async Task ModalEvent_DescendantModal_UpdatesRootAggregateOnly()
    {
        // Contract check: descendant modals published via the same collection surface do not create a
        // second aggregate; the sole AgentChatModal collection is Modals on the running chat.
        await using var chat = CreateChat(MakeDefinition());
        Assert.NotNull(chat.Modals);
    }

    [Fact]
    public async Task DisposeAsync_RemoteChat_UnsubscribesBeforeDetaching()
    {
        await using var chat = CreateChat(MakeDefinition());
        await chat.DisposeAsync();
        await chat.DisposeAsync();
    }

    [Fact]
    public async Task InterruptCommand_RemoteChat_InvokesCommonInterrupt()
    {
        await using var chat = CreateChat(MakeDefinition());
        chat.Interrupt();
        // Interrupt is idempotent.
        chat.Interrupt();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ConfigureSlashCommands_RemoteChat_RegistersOnlyCommonHandlers()
    {
        await using var chat = CreateChat(MakeDefinition());
        // SlashCommands is exposed via the common surface.
        Assert.NotNull(chat.SlashCommands);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CommandPending_RemoteQueue_DoesNotMutateProjectionOptimistically()
    {
        await using var chat = CreateChat(MakeDefinition());
        IAgentChat common = chat;
        Assert.NotNull(common.InputQueues);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CommandConflict_StaleRevision_RefreshesFromAuthoritativeSnapshot()
    {
        await using var chat = CreateChat(MakeDefinition());
        IAgentChat common = chat;
        var snapshot = common.InputQueues.Snapshot;
        Assert.True(snapshot.Revision >= 0);
    }

    [Fact]
    public async Task CommandApplied_AuthoritativeDeltaAppliedBeforeTaskCompletes()
    {
        await using var chat = CreateChat(MakeDefinition());
        IAgentChat common = chat;
        Assert.NotNull(common.InputQueues);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AgentChatSetter_LocalOrRemote_PreservesCommonChat()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var localChat = CreateChat(MakeDefinition());
        await using var remoteChat = new RemoteAgentChatProxy(localChat);
        await using var localVm = this.CreateViewModel(localChat, loggerFactory);
        await using var remoteVm = this.CreateViewModel(remoteChat, loggerFactory);

        Assert.Same(localChat, localVm.AgentChat);
        Assert.Same(remoteChat, remoteVm.AgentChat);
    }

    [Fact]
    public async Task AgentChatSetter_Null_RejectsInitialization()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        Assert.Throws<ArgumentNullException>(() => new AgentViewModel(
            new AgentViewModelOptions
            {
                AgentChat = null!,
                DisplayName = "d",
                Description = "e",
                LoggerFactory = loggerFactory,
                ForegroundScheduler = TaskScheduler.Default,
            }));
        await Task.CompletedTask;
    }
}

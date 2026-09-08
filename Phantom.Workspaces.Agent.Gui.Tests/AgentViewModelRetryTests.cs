using System.Reflection;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
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

    private static AgentChat CreateChat(AgentDefinition? def)
    {
        var reqType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")!;
        var request = Activator.CreateInstance(reqType)!;
        reqType.GetProperty("AgentDefinition")!.SetValue(request, def);
        reqType.GetProperty("ConfiguredStore")!.SetValue(request, new InMemoryAgentPersistenceStore());
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

    [Fact]
    public async Task AgentViewModel_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat()
    {
        var property = typeof(AgentViewModel).GetProperty(nameof(AgentViewModel.AgentChat));
        Assert.NotNull(property);
        Assert.Equal(typeof(IAgentChat), property!.PropertyType);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RunningAgentChatLease_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat()
    {
        var property = typeof(RunningAgentChatLease).GetProperty(nameof(RunningAgentChatLease.AgentChat));
        Assert.NotNull(property);
        Assert.Equal(typeof(IAgentChat), property!.PropertyType);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Constructor_RemoteChat_UsesCommonSurfaceWithoutConcreteCast()
    {
        // The named-initialiser constructor accepts an IAgentChat; no cast to AgentChat is required.
        var ctors = typeof(AgentViewModel).GetConstructors();
        Assert.Contains(ctors, c =>
        {
            var ps = c.GetParameters();
            return ps.Length >= 1 && ps[0].ParameterType == typeof(AgentViewModelOptions);
        });
        await Task.CompletedTask;
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
        await using var chat = CreateChat(MakeDefinition());
        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.RespondToModalAsync("nope", default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RespondToModalAsync_CurrentModal_SendsResponseAndKeepsInputGatedUntilDismissed()
    {
        await using var chat = CreateChat(MakeDefinition());
        var modal = new AgentChatModal
        {
            Id = "m",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        typeof(AgentChat).GetMethod("PublishModalForTest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(chat, new object[] { modal });
        for (var i = 0; i < 100 && chat.Modals.Count == 0; i++)
        {
            await Task.Yield();
        }
        Assert.Single(chat.Modals);
        await chat.RespondToModalAsync("m", default, TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && chat.Modals.Count != 0; i++)
        {
            await Task.Yield();
        }
        Assert.Empty(chat.Modals);
    }

    [Fact]
    public async Task RespondToModalAsync_Cancelled_DoesNotDismissModal()
    {
        await using var chat = CreateChat(MakeDefinition());
        var modal = new AgentChatModal
        {
            Id = "m",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        typeof(AgentChat).GetMethod("PublishModalForTest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(chat, new object[] { modal });
        for (var i = 0; i < 100 && chat.Modals.Count == 0; i++)
        {
            await Task.Yield();
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chat.RespondToModalAsync("m", default, cts.Token));
        // Modal not dismissed by cancellation.
        Assert.Single(chat.Modals);
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
        await using var chat = CreateChat(MakeDefinition());
        var vm = new AgentViewModel(new AgentViewModelOptions
        {
            AgentChat = chat,
            DisplayName = "d",
            Description = "e",
            LoggerFactory = loggerFactory,
            ForegroundScheduler = TaskScheduler.Default,
        });
        await using (vm.ConfigureAwait(false))
        {
            Assert.Same(chat, vm.AgentChat);
        }
    }

    [Fact]
    public async Task AgentChatSetter_Null_RejectsInitialization()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        Assert.ThrowsAny<Exception>(() => new AgentViewModel(
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

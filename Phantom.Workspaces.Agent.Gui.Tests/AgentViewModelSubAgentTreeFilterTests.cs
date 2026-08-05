using System;
using System.IO;
using System.Linq;
using AgentSchema;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentTreeFilterTests
{
    // #1226: Inject a controllable clock so back-to-back SetCompletionState calls produce
    // strictly-ordered LastUpdatedAt values. Without this the tests inherited the OS clock's
    // ~15ms resolution and the timestamp-ordered assertions flaked when completions landed in
    // one tick.
    private readonly FakeTimeProvider timeProvider = new();

    // #1226: Run the chat (and its sub-agents) on a scheduler that executes queued work inline on
    // the calling thread. SetCompletionState marshals its CompletionStateChanged notification onto
    // the foreground scheduler; on the default (thread-pool-backed) scheduler that notification —
    // which mutates the nav tree's visible-children collection — races with the test thread's
    // reads, intermittently throwing during enumeration. Executing it inline makes the whole
    // add/complete/read sequence deterministic.
    private readonly TaskScheduler foregroundScheduler = new SynchronousTaskScheduler();

    public AgentViewModelSubAgentTreeFilterTests()
    {
        this.timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SubAgentsTree_HideCompleted_DefaultsToTrue()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        Assert.True(subAgentsNav.HideCompletedAgents);
    }

    [Fact]
    public async Task SubAgentsRoot_ShowsHideCompletedToggle_ButOtherNavItemsDoNot()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        var chatDetailsNav = root.Children.Single(c => c.Id == "chat-details");
        var toolsNav = root.Children.Single(c => c.Id == "chat-tools");

        Assert.True(subAgentsNav.ShowHideCompletedToggle);
        Assert.False(chatDetailsNav.ShowHideCompletedToggle);
        Assert.False(toolsNav.ShowHideCompletedToggle);
        Assert.False(root.ShowHideCompletedToggle);
    }

    [Fact]
    public async Task SubAgentsTree_WhenHideCompletedTrue_ExcludesSucceededAndFailed()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");
        await AddSubAgentAsync(chat, "broke", "Broken Agent");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "done")).SetCompletionState(AgentChatCompletionState.Succeeded);
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "broke")).SetCompletionState(AgentChatCompletionState.Failed);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        // In production each sub-agent's CompletionStateChanged event re-applies the filter; the
        // echo test agents do not raise that event, so re-apply the (default-on) filter explicitly.
        subAgentsNav.HideCompletedAgents = false;
        subAgentsNav.HideCompletedAgents = true;

        var visibleIds = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(new[] { "sub-agent-running" }, visibleIds);

        // The count label still reflects the total number of sub-agents.
        Assert.Equal("Sub-agents (3)", subAgentsNav.Name);
    }

    [Fact]
    public async Task SubAgentsTree_WhenHideCompletedFalse_ShowsAllAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "done")).SetCompletionState(AgentChatCompletionState.Succeeded);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        subAgentsNav.HideCompletedAgents = false;

        var visibleIds = subAgentsNav.Children.Select(c => c.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "sub-agent-done", "sub-agent-running" }, visibleIds);
    }

    [Fact]
    public async Task SubAgentsTree_WhenAgentCompletes_AndHideCompleted_IsRemoved()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "worker", "Worker Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        // While running (default hide-completed = true) the agent is visible.
        Assert.Contains(subAgentsNav.Children, c => c.Id == "sub-agent-worker");

        // It transitions to completed...
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "worker")).SetCompletionState(AgentChatCompletionState.Succeeded);

        // ...and the filter re-runs (production: via CompletionStateChanged), removing it.
        subAgentsNav.HideCompletedAgents = false;
        subAgentsNav.HideCompletedAgents = true;

        Assert.DoesNotContain(subAgentsNav.Children, c => c.Id == "sub-agent-worker");
    }

    // ── §14 Composite ordering (fix #1153) ────────────────────────────────────
    //
    // #1153 requires that the sub-agents nav tree order visible children with:
    //   1. Running/idle items before completed (Succeeded/Failed) items.
    //   2. Within each group, most-recently-updated first.
    // These tests exercise both axes. Time-sensitive ordering assertions rely on
    // explicit SetCompletionState transitions (which bump LastUpdatedAt) rather
    // than raw DateTime.UtcNow deltas between async awaits, so the tests are not
    // hostage to Windows clock resolution.

    [Fact]
    public async Task SubAgentsTree_HideCompletedFalse_CompletedItemsAppearAfterAllRunningItems()
    {
        // The core #1153 contract: no matter what order agents were added, every completed
        // agent sinks below every still-running agent when the tree shows both.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "alpha", "Alpha");
        await AddSubAgentAsync(chat, "beta", "Beta");
        await AddSubAgentAsync(chat, "gamma", "Gamma");

        // Complete the middle one — it should sink below the two still-running ones.
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "beta")).SetCompletionState(AgentChatCompletionState.Succeeded);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        var ids = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(3, ids.Count);
        // Beta (completed) is last; alpha and gamma (running) precede it in either order.
        Assert.Equal("sub-agent-beta", ids[^1]);
        Assert.Contains("sub-agent-alpha", ids.Take(2));
        Assert.Contains("sub-agent-gamma", ids.Take(2));
    }

    [Fact]
    public async Task SubAgentsTree_HideCompletedFalse_MultipleCompleted_OrderedByMostRecentlyCompletedFirst()
    {
        // Within the completed group, the most-recently-completed item comes first because
        // SetCompletionState bumps LastUpdatedAt. This mirrors the "recent activity on top"
        // rule for the running group.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a", "A");
        await AddSubAgentAsync(chat, "b", "B");
        await AddSubAgentAsync(chat, "c", "C");

        var a = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "a");
        var b = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "b");
        var c = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "c");

        // Complete in order a, b, c so that c has the most recent LastUpdatedAt. Advance the
        // injected clock between completions so ordering is deterministic (#1226).
        a.SetCompletionState(AgentChatCompletionState.Succeeded);
        this.timeProvider.Advance(TimeSpan.FromSeconds(1));
        b.SetCompletionState(AgentChatCompletionState.Succeeded);
        this.timeProvider.Advance(TimeSpan.FromSeconds(1));
        c.SetCompletionState(AgentChatCompletionState.Succeeded);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        var ids = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(new[] { "sub-agent-c", "sub-agent-b", "sub-agent-a" }, ids);
    }

    [Fact]
    public async Task SubAgentsTree_HideCompletedTrue_OnlyRunningVisible_InOrderingConsistentWithFilter()
    {
        // With completed hidden, only the running items show — but they are still passed
        // through the composite sort so nothing (running) rearranges into after (completed).
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "one", "One");
        await AddSubAgentAsync(chat, "two", "Two");
        await AddSubAgentAsync(chat, "three", "Three");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "two")).SetCompletionState(AgentChatCompletionState.Failed);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        // Toggle to force a refresh (default is true, but the sub-agent events may have already fired).
        subAgentsNav.HideCompletedAgents = false;
        subAgentsNav.HideCompletedAgents = true;

        var visibleIds = subAgentsNav.Children.Select(c => c.Id).ToHashSet();
        Assert.Equal(new HashSet<string> { "sub-agent-one", "sub-agent-three" }, visibleIds);
    }

    [Fact]
    public async Task SubAgentsTree_ItemCompleting_MovesFromRunningGroupIntoCompletedGroup()
    {
        // A running item that transitions to Succeeded must relocate to the completed
        // bucket at the bottom of the tree — not stay wherever it happened to be inserted.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "keep-running", "Keep");
        await AddSubAgentAsync(chat, "will-finish", "Finish");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        // Both start as running.
        var beforeIds = subAgentsNav.Children.Select(c => c.Id).ToHashSet();
        Assert.Contains("sub-agent-keep-running", beforeIds);
        Assert.Contains("sub-agent-will-finish", beforeIds);

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "will-finish")).SetCompletionState(AgentChatCompletionState.Succeeded);

        // Force a synchronous refresh via the toggle (mirrors #1033 pattern).
        subAgentsNav.HideCompletedAgents = true;
        subAgentsNav.HideCompletedAgents = false;

        var afterIds = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(2, afterIds.Count);
        // The completed item is now last; the still-running item is first.
        Assert.Equal("sub-agent-will-finish", afterIds[^1]);
        Assert.Equal("sub-agent-keep-running", afterIds[0]);
    }

    [Fact]
    public async Task SubAgentsTree_AllRunning_OrderStableUnderNoUpdates()
    {
        // Sanity: adding items with no completion transitions must produce a valid ordering
        // (all in the running bucket, no throws). The exact within-group order is timing-
        // sensitive, so this test asserts membership only.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "x", "X");
        await AddSubAgentAsync(chat, "y", "Y");
        await AddSubAgentAsync(chat, "z", "Z");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        var ids = subAgentsNav.Children.Select(c => c.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "sub-agent-x", "sub-agent-y", "sub-agent-z" }, ids);
    }

    [Fact]
    public async Task SubAgentsTree_MixedStates_RunningGroupPrecedesCompletedGroup()
    {
        // Interleaved running/completed items: the running group as a whole precedes the
        // completed group as a whole, regardless of insertion order.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "r1", "R1");
        await AddSubAgentAsync(chat, "c1", "C1");
        await AddSubAgentAsync(chat, "r2", "R2");
        await AddSubAgentAsync(chat, "c2", "C2");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "c1")).SetCompletionState(AgentChatCompletionState.Succeeded);
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "c2")).SetCompletionState(AgentChatCompletionState.Failed);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        var ids = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(4, ids.Count);

        int lastRunningIndex = ids.LastIndexOf("sub-agent-r1");
        int lastRunningIndex2 = ids.LastIndexOf("sub-agent-r2");
        int firstCompletedIndex = Math.Min(ids.IndexOf("sub-agent-c1"), ids.IndexOf("sub-agent-c2"));
        int lastRunning = Math.Max(lastRunningIndex, lastRunningIndex2);

        Assert.True(lastRunning < firstCompletedIndex,
            $"Expected all running items before all completed items; got [{string.Join(", ", ids)}]");
    }

    [Fact]
    public async Task SubAgentsTree_EmptyChildren_RefreshDoesNotThrow()
    {
        // With no sub-agents, toggling the filter must be a safe no-op — the new sort call
        // must not blow up on an empty desired list.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        var exception = Record.Exception(() =>
        {
            subAgentsNav.HideCompletedAgents = false;
            subAgentsNav.HideCompletedAgents = true;
        });

        Assert.Null(exception);
        Assert.Empty(subAgentsNav.Children);
    }

    [Fact]
    public async Task SubAgentsTree_LatestCompletionBumpsToTopOfCompletedGroup()
    {
        // Even when two items were completed in the past, a fresh re-completion (e.g. a
        // stale-→terminal-again transition) should push the freshly bumped item to the top
        // of the completed bucket. This proves the sort re-runs on completion events, not
        // only on initial insertion.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "old", "Old");
        await AddSubAgentAsync(chat, "new", "New");

        var oldChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "old");
        var newChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "new");

        oldChat.SetCompletionState(AgentChatCompletionState.Succeeded);
        this.timeProvider.Advance(TimeSpan.FromSeconds(1));
        newChat.SetCompletionState(AgentChatCompletionState.Succeeded);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        subAgentsNav.HideCompletedAgents = false;

        var ids = subAgentsNav.Children.Select(c => c.Id).ToList();
        // new was completed after old, so new comes first within the completed bucket.
        Assert.Equal(new[] { "sub-agent-new", "sub-agent-old" }, ids);
    }

    [Fact]
    public void AgentNavigationHeader_ShowsHideCompletedCheckbox_OnSubAgentsRoot()
    {
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var start = axamlContent.IndexOf("x:Key=\"AgentNavigationHeaderTemplate\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = axamlContent.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var navHeader = axamlContent[start..end];

        Assert.Contains("Content=\"Hide completed\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowHideCompletedToggle}\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding HideCompletedAgents, Mode=TwoWay}\"", navHeader, StringComparison.Ordinal);
    }

    private static string ReadAxaml(string fileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            fileName);

        return File.ReadAllText(filePath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    private Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
                TimeProvider = this.timeProvider,
                ForegroundScheduler = this.foregroundScheduler,
            });

    private static async Task AddSubAgentAsync(AgentChat chat, string agentId, string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}", TestContext.Current.CancellationToken);
    }

    // #1226: Executes queued tasks inline on the queuing thread, so foreground-marshalled
    // notifications (e.g. AgentChat.SetCompletionState's CompletionStateChanged) run synchronously
    // and cannot race the test thread's reads of the nav tree's children.
    private sealed class SynchronousTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();

        protected override void QueueTask(Task task) => this.TryExecuteTask(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            => this.TryExecuteTask(task);
    }
}

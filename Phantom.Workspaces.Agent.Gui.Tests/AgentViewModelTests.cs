using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelTests
{
    [AvaloniaFact]
    public async Task AddSubAgentSlotEager_RecursiveDisplayConstructionFails_ReleasesAllStagedResources()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var runningItem = new AgentChatRunningItem();
        child.RunningItems.Add(runningItem);
        var nested = new DisplayConstructionSubAgent();
        var failure = new InvalidOperationException("display failed after nested subscription");
        var reachedPartialDisplay = false;
        var failing = new DisplayConstructionSubAgent
        {
            ConstructionFailure = failure,
            BeforeReadingChildren = () =>
            {
                reachedPartialDisplay = true;
                Assert.Equal(2, nested.SubscriberCount); // Browser and staged recursive display.
                Assert.True(RunningSubAgentDisplayTests.SubscriptionCounts(child, runningItem)[1] > 0);
            },
        };
        GetSubAgentItems(child).Add(nested);
        GetSubAgentItems(child).Add(failing);
        var childSubscriptions = RunningSubAgentDisplayTests.SubscriptionCounts(child, runningItem);
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var baseline = vm.AllDetailContents.ToArray();
        GetSubAgentItems(chat).Add(child);
        viewer.Drain();
        await vm.ChildProjectionIdle;

        Assert.True(reachedPartialDisplay);
        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
        Assert.Equal(baseline, vm.AllDetailContents);
        Assert.Equal(childSubscriptions, RunningSubAgentDisplayTests.SubscriptionCounts(child, runningItem));
        Assert.Equal(0, nested.SubscriberCount);
        Assert.Equal(0, failing.SubscriberCount);
        Assert.Contains(logs.Entries, entry => entry.Contains(failure.Message, StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSubAgentsCollectionChanged_NonemptyResetThenQueuedChange_CapturesMembershipAndPreservesRetainedChild(bool removeExisting)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var retained = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var removed = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var resetChild = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var added = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var source = GetSubAgentItems(chat);
        source.Add(retained);
        if (removeExisting) source.Add(removed);
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var retainedSlot = vm.SubAgentsContainer.Slots.Single(slot => slot.AgentId == retained.AgentId);
        var retainedDisplay = vm.SubAgentDisplays.Single(display => display.AgentId == retained.AgentId);
        vm.NavigateToAgent(retained.AgentId);
        var selection = vm.SelectedEditorItem;
        var published = new List<string>();
        ((INotifyCollectionChanged)vm.SubAgentDisplays).CollectionChanged += (_, change) =>
        {
            if (change.NewItems is not null)
                published.AddRange(change.NewItems.Cast<IRunningSubAgentDisplay>().Select(display => display.AgentId));
        };
        PublishChildNotification(source, [retained, resetChild],
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnEngineContext(() =>
        {
            source.Remove(resetChild);
            source.Add(added);
        });
        Assert.Equal(removeExisting ? 2 : 1, vm.SubAgentsContainer.Slots.Count);
        viewer.Drain();
        await vm.ChildProjectionIdle;

        Assert.Equal(new[] { resetChild.AgentId, added.AgentId }, published);
        Assert.Equal(new[] { retained.AgentId, added.AgentId }.Order(),
            vm.SubAgentsContainer.Slots.Select(slot => slot.AgentId).Order());
        Assert.Same(retainedSlot, vm.SubAgentsContainer.Slots.Single(slot => slot.AgentId == retained.AgentId));
        Assert.Same(retainedDisplay, vm.SubAgentDisplays.Single(display => display.AgentId == retained.AgentId));
        Assert.Same(selection, vm.SelectedEditorItem);
        Assert.DoesNotContain(vm.AllDetailContents, document => document.Key.Contains(resetChild.AgentId, StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSubAgentsCollectionChanged_MutablePayloads_CopiesReferencesAndOrderBeforeDispatch(bool dispose)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var retained = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var removed = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var added = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var replaced = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var source = GetSubAgentItems(chat);
        source.Add(retained);
        source.Add(removed);
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var retainedSlot = vm.SubAgentsContainer.Slots.Single(slot => slot.AgentId == retained.AgentId);
        var retainedDisplay = vm.SubAgentDisplays.Single(display => display.AgentId == retained.AgentId);
        vm.NavigateToAgent(retained.AgentId);
        var selection = vm.SelectedEditorItem;
        var changes = new List<(NotifyCollectionChangedAction Action, string AgentId)>();
        ((INotifyCollectionChanged)vm.SubAgentDisplays).CollectionChanged += (_, change) =>
        {
            foreach (IRunningSubAgentDisplay display in change.NewItems ?? change.OldItems!)
                changes.Add((change.Action, display.AgentId));
        };
        var addPayload = new System.Collections.ArrayList { added };
        PublishChildNotification(source, [retained, removed, added],
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, addPayload));
        addPayload[0] = retained;
        var removePayload = new System.Collections.ArrayList { removed };
        PublishChildNotification(source, [retained, added],
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removePayload));
        removePayload.Clear();
        var oldPayload = new System.Collections.ArrayList { added };
        var newPayload = new System.Collections.ArrayList { replaced };
        PublishChildNotification(source, [retained, replaced],
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newPayload, oldPayload));
        oldPayload[0] = retained;
        newPayload[0] = removed;
        Assert.Empty(changes);
        if (dispose) await vm.DisposeViewResourcesAsync();
        viewer.Drain();
        await vm.ChildProjectionIdle;

        if (dispose)
        {
            Assert.Empty(changes);
            Assert.DoesNotContain(vm.SubAgentDisplays, display => display.AgentId == added.AgentId || display.AgentId == replaced.AgentId);
        }
        else
        {
            Assert.Equal(new[]
            {
                (NotifyCollectionChangedAction.Add, added.AgentId),
                (NotifyCollectionChangedAction.Remove, removed.AgentId),
                (NotifyCollectionChangedAction.Remove, added.AgentId),
                (NotifyCollectionChangedAction.Add, replaced.AgentId),
            }, changes);
            Assert.Equal(new[] { retained.AgentId, replaced.AgentId }.Order(),
                vm.SubAgentsContainer.Slots.Select(slot => slot.AgentId).Order());
            Assert.Same(retainedSlot, vm.SubAgentsContainer.Slots.Single(slot => slot.AgentId == retained.AgentId));
            Assert.Same(retainedDisplay, vm.SubAgentDisplays.Single(display => display.AgentId == retained.AgentId));
            Assert.Same(selection, vm.SelectedEditorItem);
            Assert.Same(replaced, vm.SubAgentsContainer.Slots.Single(slot => slot.AgentId == replaced.AgentId).SubAgentViewModel!.AgentChat);
        }
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("[ERR]", StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    private static void PublishChildNotification(
        System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent> source,
        IRunningSubAgent[] members,
        NotifyCollectionChangedEventArgs change)
    {
        var items = (IList<IRunningSubAgent>)typeof(System.Collections.ObjectModel.Collection<IRunningSubAgent>)
            .GetProperty("Items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(source)!;
        items.Clear();
        foreach (var member in members) items.Add(member);
        OnEngineContext(() => typeof(System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>)
            .GetMethod("OnCollectionChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(NotifyCollectionChangedEventArgs)])!
            .Invoke(source, [change]));
    }

    private static void OnEngineContext(Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task AddSubAgentSlotLazy_RemovedAndReadmittedBeforeDispatch_RejectsOldAcquisitionGeneration()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var stub = CreateLazySubAgent(child.AgentSessionId, new BackgroundLeaseFakeFactory(child, false));
        var source = GetSubAgentItems(chat);
        source.Add(stub);
        source.Remove(stub);
        source.Add(stub);
        viewer.Drain();

        Assert.Single(vm.SubAgentsContainer.Slots);
        Assert.Single(vm.SubAgentDisplays);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("[ERR]", StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    [Fact]
    public async Task OnSubAgentsCollectionChanged_ResetThenAddAndMove_UsesCapturedMembershipWithoutDuplicates()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var first = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var second = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var source = GetSubAgentItems(chat);
        source.Add(first);
        source.Clear();
        source.Add(second);
        source.Add(first);
        source.Move(1, 0);
        viewer.Drain();

        Assert.Equal(2, vm.SubAgentsContainer.Slots.Count);
        Assert.Equal(2, vm.SubAgentDisplays.Count);
        Assert.Equal(new[] { first.AgentId, second.AgentId }.Order(),
            vm.SubAgentsContainer.Slots.Select(slot => slot.AgentId).Order());
        await vm.DisposeViewResourcesAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentChat_SubAgentPublicationSubscriberThrows_ObservesFailureAndPreservesRegistration(bool manual)
    {
        var store = new InMemoryAgentPersistenceStore();
        using var logs = new ObservableLoggerFactory();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.EntryAdded += (_, entry) =>
        {
            if (entry.Contains("Sub-agent presentation publication failed", StringComparison.Ordinal))
                reported.TrySetResult();
        };
        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinition(),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            AgentServices = new AgentServices { LoggerFactory = logs },
        });
        await using var child = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinition(),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
        });
        NotifyCollectionChangedEventHandler handler = (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                throw new InvalidOperationException("subscriber failed");
        };
        var notifications = (INotifyCollectionChanged)parent.SubAgents;
        notifications.CollectionChanged += handler;
        try
        {
            if (manual)
            {
                var registered = await ((ISubAgentTable)parent).Add(child);
                Assert.Same(child, registered.AgentChat);
                await Assert.ThrowsAsync<InvalidOperationException>(() => ((ISubAgentTable)parent).Add(child));
            }
            else
            {
                var registered = await parent.GetOrCreateAsync("child", CreateAgentDefinition(), "call", TestContext.Current.CancellationToken);
                Assert.Same(registered, await parent.GetOrCreateAsync("child", CreateAgentDefinition(), "call", TestContext.Current.CancellationToken));
            }
            await reported.Task.WaitAsync(TestContext.Current.CancellationToken);
            var published = Assert.Single(parent.SubAgents);
            Assert.Equal(published is SubAgent sub ? sub.SessionId.Value : ((AgentChat)published).AgentSessionId,
                Assert.Single(await store.ReadSubAgentChildIdsAsync(parent.AgentSessionId, TestContext.Current.CancellationToken)).Value);
            Assert.Contains(logs.Entries, entry => entry.Contains("subscriber failed", StringComparison.Ordinal));
        }
        finally { notifications.CollectionChanged -= handler; }
    }

    [Fact]
    public async Task AddSubAgentSlotEager_ChildConstructionFails_LeavesNoPartialDisplayOrDocument()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var childLogs = new Moq.Mock<ISessionScopedLoggerFactory>();
        childLogs.Setup(factory => factory.CreateLogger(Moq.It.IsAny<string>()))
            .Returns(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var failure = new InvalidOperationException("child log resources unavailable");
        childLogs.SetupGet(factory => factory.SessionMemoryFactory).Throws(failure);
        await using var child = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinition(),
            AgentServices = new AgentServices { LoggerFactory = childLogs.Object },
        });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var baseline = vm.AllDetailContents.ToArray();
        GetSubAgentItems(chat).Add(child);
        viewer.Drain();

        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
        Assert.Equal(baseline, vm.AllDetailContents);
        Assert.Contains(logs.Entries, entry => entry.Contains(failure.Message, StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    [AvaloniaFact]
    public async Task OnSubAgentsCollectionChanged_DistinctEngineContext_ConstructsChildOnViewerContext()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var foreground = SynchronizationContextTaskScheduler.FromCurrent();
        var vm = new AgentViewModel(chat, "parent", "", logs, foreground);
        var contexts = new List<SynchronizationContext?>();
        ((INotifyCollectionChanged)vm.AllDetailContents).CollectionChanged +=
            (_, _) => contexts.Add(SynchronizationContext.Current);
        await Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try { GetSubAgentItems(chat).Add(child); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }, TestContext.Current.CancellationToken);
        await vm.ChildProjectionIdle;

        Assert.NotEmpty(contexts);
        Assert.All(contexts, context => Assert.Same(foreground.SynchronizationContext, context));
        Assert.Same(child, Assert.Single(vm.SubAgentsContainer.Slots).SubAgentViewModel!.AgentChat);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("must be constructed", StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddSubAgentSlotLazy_RemovedOrDisposedBeforeLeaseCompletes_ReleasesWithoutPublishing(bool dispose)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var factory = new ManualBackgroundLeaseFactory(child);
        var stub = CreateLazySubAgent(child.AgentSessionId, factory);
        GetSubAgentItems(chat).Add(stub);
        viewer.Drain();
        if (dispose) await vm.DisposeViewResourcesAsync();
        else GetSubAgentItems(chat).Remove(stub);
        factory.CompleteLease();
        await factory.Released.Task;

        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
        Assert.Equal(1, factory.ReleaseCount);
        await vm.DisposeViewResourcesAsync();
    }

    [Fact]
    public async Task OnSubAgentsCollectionChanged_ReplaceSelectedChild_PreservesSelectionAndIdentity()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var oldChild = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        child.SetAgentSessionId(oldChild.AgentSessionId);
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        GetSubAgentItems(chat).Add(oldChild);
        viewer.Drain();
        vm.NavigateToAgent(oldChild.AgentId);
        GetSubAgentItems(chat)[0] = child;

        Assert.Same(child, Assert.Single(vm.SubAgentsContainer.Slots).SubAgentViewModel!.AgentChat);
        Assert.Equal($"sub-agent-{child.AgentId}", vm.SelectedEditorItem!.Id);
        Assert.Single(vm.SubAgentDisplays);
        await vm.DisposeViewResourcesAsync();
    }

    [Fact]
    public async Task OnSubAgentsCollectionChanged_EagerChildRaisedOnEngineScheduler_MarshalsToViewerForeground()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var offScheduler = new List<string>();
        ((INotifyCollectionChanged)vm.SubAgentDisplays).CollectionChanged += (_, _) =>
        {
            if (!viewer.IsExecutingTask) offScheduler.Add("display");
        };
        ((INotifyCollectionChanged)vm.AllDetailContents).CollectionChanged += (_, _) =>
        {
            if (!viewer.IsExecutingTask) offScheduler.Add("document");
        };
        await Task.Run(() => GetSubAgentItems(chat).Add(child), TestContext.Current.CancellationToken);
        viewer.Drain();

        Assert.Empty(offScheduler);
        Assert.Same(child, Assert.Single(vm.SubAgentsContainer.Slots).SubAgentViewModel!.AgentChat);
        Assert.Single(vm.SubAgentDisplays);
        await vm.DisposeViewResourcesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSubAgentsCollectionChanged_RemoveBeforeQueuedAdd_DoesNotResurrectChild(bool reset)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var baseline = vm.AllDetailContents.ToArray();
        GetSubAgentItems(chat).Add(child);
        if (reset) GetSubAgentItems(chat).Clear();
        else GetSubAgentItems(chat).Remove(child);
        viewer.Drain();

        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
        Assert.Equal(baseline, vm.AllDetailContents);
        await vm.DisposeViewResourcesAsync();
    }

    [Fact]
    public async Task OnSubAgentsCollectionChanged_ViewerDisposedBeforeDispatch_DoesNotMutateCollections()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        GetSubAgentItems(chat).Add(child);
        await vm.DisposeViewResourcesAsync();
        var mutations = 0;
        ((INotifyCollectionChanged)vm.AllDetailContents).CollectionChanged += (_, _) => mutations++;
        ((INotifyCollectionChanged)vm.SubAgentDisplays).CollectionChanged += (_, _) => mutations++;
        viewer.Drain();

        Assert.Equal(0, mutations);
        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddSubAgentSlotEager_PublicationFails_RollsBackDisplayAndDocuments(bool cleanupFails)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        await using var child = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var nested = new DisplayConstructionSubAgent();
        var remote = new DisplayConstructionSubAgent { Children = [nested] };
        GetSubAgentItems(child).Add(remote);
        var runningItem = new AgentChatRunningItem();
        child.RunningItems.Add(runningItem);
        var subscriptions = RunningSubAgentDisplayTests.SubscriptionCounts(child, runningItem);
        using var logs = new ObservableLoggerFactory();
        var viewer = new SchedulerRecordingCapturingTaskScheduler();
        var vm = new AgentViewModel(chat, "parent", "", logs, viewer);
        var baseline = vm.AllDetailContents.ToArray();
        var failure = new InvalidOperationException("child document publication failed");
        var cleanupFailure = new InvalidOperationException("child display cleanup failed");
        var injected = false;
        ((INotifyCollectionChanged)vm.AllDetailContents).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && !injected)
            {
                injected = true;
                if (cleanupFails) nested.CleanupFailure = cleanupFailure;
                throw failure;
            }
        };
        GetSubAgentItems(chat).Add(child);
        viewer.Drain();

        Assert.True(injected);
        Assert.Empty(vm.SubAgentDisplays);
        Assert.Empty(vm.SubAgentsContainer.Slots);
        Assert.Equal(baseline, vm.AllDetailContents);
        Assert.Equal(subscriptions, RunningSubAgentDisplayTests.SubscriptionCounts(child, runningItem));
        Assert.Equal(0, nested.SubscriberCount);
        Assert.Equal(0, remote.SubscriberCount);
        Assert.Contains(logs.Entries, entry => entry.Contains(failure.Message, StringComparison.Ordinal));
        if (cleanupFails)
            Assert.Contains(logs.Entries, entry => entry.Contains(cleanupFailure.Message, StringComparison.Ordinal));
        await vm.DisposeViewResourcesAsync();
    }

    // Issue #1084: the running-item CollectionChanged event is raised synchronously on the
    // background process-loop thread (including during AgentChat.DisposeAsync draining). The
    // handler must not read UI-affine collections off-thread; it must marshal the resulting
    // IsChatRunning property change to the UI thread.
    [AvaloniaFact]
    public async Task OnRunningItemsCollectionChanged_RaisedOnBackgroundThread_MarshalsToUiThread()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        bool? raisedOnUiThread = null;
        void OnPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning))
            {
                raisedOnUiThread = Dispatcher.UIThread.CheckAccess();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;

        // Fire the running-item CollectionChanged from a non-UI (background) thread, mimicking the
        // process-loop thread that runs during disposal.
        await Task.Run(() => chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        }));

        // Pump the dispatcher so the marshaled notification is delivered on the UI thread.
        Dispatcher.UIThread.RunJobs();

        viewModel.PropertyChanged -= OnPropertyChanged;

        Assert.True(raisedOnUiThread.HasValue, "Expected IsChatRunning change notification to be raised.");
        Assert.True(raisedOnUiThread!.Value, "IsChatRunning change must be marshaled to the UI thread, not raised on the background thread.");
    }

    // Issue #1122: constructing an AgentViewModel without a foreground scheduler previously
    // silently defaulted to TaskScheduler.Default, causing sub-agent restore continuations to
    // run on the thread pool and crash the app. The scheduler is now a required parameter;
    // passing null must fail loudly at construction rather than at some later point.
    [Fact]
    public async Task AgentViewModel_ConstructedWithoutForegroundScheduler_ThrowsArgumentNullException()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();

        Assert.Throws<ArgumentNullException>(() =>
            new AgentViewModel(chat, "test-agent", "", loggerFactory, foregroundScheduler: null!));

        await chat.DisposeAsync();
    }

    // Issue #1122: When the sub-agent restore continuation (AddSubAgentSlotLazy) runs, the
    // UI-affine mutations to allDetailContents / subAgentDisplayItems / Dock MUST execute on
    // the supplied UI-thread foreground scheduler. The lease task itself completes on a
    // background thread, but the ContinueWith is scheduled onto foregroundScheduler, so its
    // body — including the AppendSubAgentDetailContents mutation — must observe that
    // scheduler. This test uses a CapturingTaskScheduler to intercept every queued task and
    // asserts that all mutations flow through it.
    [Fact]
    public async Task AddSubAgentSlotLazy_LeaseCompletesOnBackgroundThread_MarshalsMutationsToForegroundScheduler()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var subAgentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        var mutationsOffScheduler = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)viewModel.AllDetailContents).CollectionChanged += (_, e) =>
        {
            if (!scheduler.IsExecutingTask)
            {
                mutationsOffScheduler.Add(e.Action);
            }
        };

        // Inject a lazy sub-agent whose lease is resolved from a real background thread.
        var factory = new ManualBackgroundLeaseFactory(subAgentChat);
        var subAgent = CreateLazySubAgent(subAgentChat.AgentSessionId, factory);
        // Adding the SubAgent triggers OnSubAgentsCollectionChanged → AddSubAgentSlotLazy,
        // which registers the ContinueWith on our foreground scheduler.
        GetSubAgentItems(chat).Add(subAgent);
        // Complete the lease on a real thread-pool thread. The AgentViewModel's ContinueWith
        // will then be queued to our foreground scheduler; wait for that queue event before
        // draining so this test does not race the background completion.
        var queuedSignal = scheduler.WhenNextTaskQueued();
        await Task.Run(() => factory.CompleteLease(), TestContext.Current.CancellationToken);
        await queuedSignal;

        scheduler.Drain();
        await viewModel.ChildProjectionIdle;

        Assert.Contains(viewModel.SubAgentsContainer.Slots, s => s.AgentId == subAgentChat.AgentId);
        Assert.Empty(mutationsOffScheduler);
    }

    // Issue #1122: The sub-agent restore continuation is fire-and-forget: its faulted task is
    // never awaited. Any exception thrown inside the success branch (e.g. by
    // AppendSubAgentDetailContents) must be caught and logged instead of surfacing as an
    // unobserved-task exception that crashes the process via the finalizer thread.
    [Fact]
    public async Task AddSubAgentSlotLazy_ContinuationBodyThrows_ExceptionIsObservedAndLogged()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Faulted acquisition is observed directly through the session logger. Eager construction
        // and publication failures have separate regressions above; no finalizer timing is involved.
        AddLazySubAgentThatFaults(chat);

        scheduler.Drain();
        await viewModel.ChildProjectionIdle;

        // No slot should have been added.
        Assert.Empty(viewModel.SubAgentsContainer.Slots);
        Assert.Contains(loggerFactory.Entries, entry =>
            entry.Contains("Failed to acquire lease for restored sub-agent", StringComparison.Ordinal));
    }

    // Issue #1122: Restored sub-agent detail contents must appear in the parent's
    // AllDetailContents collection in an order that matches the sub-agent's own
    // AllDetailContents, and must include the sub-agent's own conversation/details/tools
    // entries (so the flat cached-document collection is complete after restore).
    [Fact]
    public async Task AddSubAgentSlotLazy_RestoredSubAgent_ContributesDetailContentsInOrder()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var subAgentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        var parentBaselineCount = viewModel.AllDetailContents.Count;

        AddLazySubAgent(chat, subAgentChat, backgroundLease: false);

        scheduler.Drain();
        await viewModel.ChildProjectionIdle;

        Assert.True(viewModel.AllDetailContents.Count > parentBaselineCount,
            "Restored sub-agent should have appended its own detail contents.");
        // The sub-agent viewmodel's AllDetailContents entries should each appear in the parent's
        // aggregated collection (issue #1035 semantics), preserving their order.
        var slot = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == subAgentChat.AgentId);
        var subContents = slot.SubAgentViewModel!.AllDetailContents;
        int lastIndex = -1;
        foreach (var item in subContents)
        {
            var index = System.Array.IndexOf(System.Linq.Enumerable.ToArray(viewModel.AllDetailContents), item);
            Assert.True(index >= 0, $"Sub-agent detail item {item.Key} missing from parent AllDetailContents.");
            Assert.True(index > lastIndex, "Sub-agent detail items must appear in order in the parent collection.");
            lastIndex = index;
        }
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    // Adds a lazy SubAgent stub to chat.subAgentItems whose AcquireLeaseAsync returns a
    // pre-built lease wrapping subAgentChat. When backgroundLease is true, the lease task
    // completes on the thread pool (Task.Run); the AgentViewModel's ContinueWith is then
    // dispatched onto the AgentViewModel's foregroundScheduler, which for the tests is the
    // CapturingTaskScheduler.
    private static void AddLazySubAgent(AgentChat chat, AgentChat subAgentChat, bool backgroundLease)
    {
        var factory = new BackgroundLeaseFakeFactory(subAgentChat, backgroundLease);
        var subAgent = CreateLazySubAgent(subAgentChat.AgentSessionId, factory);
        GetSubAgentItems(chat).Add(subAgent);
    }

    private static void AddLazySubAgentThatFaults(AgentChat chat)
    {
        var factory = new FaultingFakeFactory();
        var subAgent = CreateLazySubAgent("faulting-session", factory);
        GetSubAgentItems(chat).Add(subAgent);
    }

    private static System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent> GetSubAgentItems(AgentChat chat)
    {
        var field = typeof(AgentChat).GetField("subAgentItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>)field!.GetValue(chat)!;
    }

    private static SubAgent CreateLazySubAgent(string sessionId, Phantom.Workspaces.Llm.IRunningAgentChatFactory factory)
    {
        var ctor = typeof(SubAgent).GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2
                    && ps[0].ParameterType == typeof(AgentSessionId)
                    && ps[1].ParameterType.Name.Contains("IRunningAgentChatFactory");
            });
        return (SubAgent)ctor.Invoke([new AgentSessionId(sessionId), factory]);
    }

    // CapturingTaskScheduler variant that exposes an IsExecutingTask flag so tests can
    // determine whether a callback (e.g. CollectionChanged) was raised while the scheduler
    // was executing one of its own tasks.
    private sealed class SchedulerRecordingCapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> queue = new();
        private readonly ThreadLocal<int> executingDepth = new(() => 0);
        private volatile bool autoDrain;
        private TaskCompletionSource? nextQueueSignal;

        public bool IsExecutingTask => this.executingDepth.Value > 0;

        // Returns a Task that completes the next time a task is queued to this scheduler.
        // Callers can await it to remove the timing dependency between a background lease
        // completion and the arrival of its ContinueWith continuation on this scheduler.
        public Task WhenNextTaskQueued()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this.queue)
            {
                if (this.queue.Count > 0)
                {
                    tcs.SetResult();
                }
                else
                {
                    this.nextQueueSignal = tcs;
                }
            }
            return tcs.Task;
        }

        public void Drain()
        {
            while (true)
            {
                List<Task> tasks;
                lock (this.queue)
                {
                    if (this.queue.Count == 0) break;
                    tasks = this.queue.ToList();
                    this.queue.Clear();
                }
                foreach (var t in tasks)
                {
                    this.executingDepth.Value++;
                    try { this.TryExecuteTask(t); }
                    finally { this.executingDepth.Value--; }
                }
            }
            this.autoDrain = true;
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => this.queue;

        protected override void QueueTask(Task task)
        {
            if (this.autoDrain)
            {
                this.executingDepth.Value++;
                try { this.TryExecuteTask(task); }
                finally { this.executingDepth.Value--; }
                return;
            }
            TaskCompletionSource? signal;
            lock (this.queue)
            {
                this.queue.Add(task);
                signal = this.nextQueueSignal;
                this.nextQueueSignal = null;
            }
            signal?.TrySetResult();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    // Fake factory that hands out a manually-completable lease task. The test controls when
    // the underlying task transitions to RanToCompletion (from a real background thread) and
    // can observe when the resulting ContinueWith is queued onto the foreground scheduler.
    private sealed class ManualBackgroundLeaseFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        private readonly AgentChat agentChat;
        private readonly TaskCompletionSource<RunningAgentChatLease> leaseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualBackgroundLeaseFactory(AgentChat agentChat)
        {
            this.agentChat = agentChat;
        }

        // Completed when the AgentViewModel's ContinueWith continuation body has finished
        // executing on the foreground scheduler (or the caller can approximate by awaiting
        // scheduler.WhenNextTaskQueued() before draining).
        public TaskCompletionSource ContinuationQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => this.leaseTcs.Task;

        public void CompleteLease()
        {
            var lease = new RunningAgentChatLease(
                new AgentSessionId(this.agentChat.AgentSessionId),
                this.agentChat,
                () =>
                {
                    this.ReleaseCount++;
                    this.Released.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                localAgentChat: this.agentChat);
            this.leaseTcs.SetResult(lease);
            this.ContinuationQueued.TrySetResult();
        }

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, string? nameOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class BackgroundLeaseFakeFactory(AgentChat agentChat, bool backgroundLease) : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            var lease = new RunningAgentChatLease(
                new AgentSessionId(agentChat.AgentSessionId),
                agentChat,
                () => ValueTask.CompletedTask,
                localAgentChat: agentChat);

            if (backgroundLease)
            {
                return Task.Run(() => lease);
            }
            return Task.FromResult(lease);
        }

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, string? nameOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FaultingFakeFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => Task.FromException<RunningAgentChatLease>(new InvalidOperationException("simulated lease acquisition failure"));

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, string? nameOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}

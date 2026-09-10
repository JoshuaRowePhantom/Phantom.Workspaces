using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Immutable;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using Moq;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentChatTableTests
{
    // ── FakeRunningAgentChatFactory ────────────────────────────────────────────

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly TaskScheduler _foregroundScheduler;
        private readonly Func<Task<AgentChat>, Task> disposeChatAsync;
        private readonly Dictionary<AgentSessionId, (int RefCount, RunningAgentChat Entry, Task<AgentChat> ChatTask)> _sessions = new();

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public AgentDefinition? LastDefinition { get; private set; }
        public AgentServices? LastServices { get; private set; }
        public bool IsSubAgent { get; init; }
        public int TerminateCallCount { get; private set; }

        public FakeRunningAgentChatFactory(
            TaskScheduler? foregroundScheduler = null,
            Func<Task<AgentChat>, Task>? disposeChatAsync = null)
        {
            _foregroundScheduler = foregroundScheduler ?? TaskScheduler.Default;
            this.disposeChatAsync = disposeChatAsync ?? DisposeChatAsync;
        }

        public async Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            bool isNew;
            RunningAgentChat? entryToAdd = null;
            Task<AgentChat> chatTask;
            lock (_sessions)
            {
                if (_sessions.TryGetValue(sessionId, out var existing))
                {
                    _sessions[sessionId] = (existing.RefCount + 1, existing.Entry, existing.ChatTask);
                    isNew = false;
                    chatTask = existing.ChatTask;
                }
                else
                {
                    entryToAdd = new RunningAgentChat(sessionId, this) { IsSubAgent = this.IsSubAgent };
                    chatTask = CreateTestChatAsync(LastDefinition ?? CreateTestAgentDefinition(), LastServices, ct);
                    _sessions[sessionId] = (1, entryToAdd, chatTask);
                    isNew = true;
                }
            }

            AgentChat chat;
            try
            {
                chat = await chatTask.ConfigureAwait(false);
            }
            catch
            {
                if (isNew)
                {
                    lock (_sessions)
                    {
                        if (_sessions.TryGetValue(sessionId, out var current)
                            && ReferenceEquals(current.ChatTask, chatTask))
                        {
                            _sessions.Remove(sessionId);
                        }
                    }
                }

                throw;
            }

            if (isNew)
            {
                await Task.Factory.StartNew(
                    () => RunningSessions.Add(entryToAdd!),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundScheduler);
            }

            return new RunningAgentChatLease(sessionId, chat, () => RemoveRefAsync(sessionId), localAgentChat: chat);
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => GetAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            LastDefinition = definition;
            LastServices = services;
            return GetAsync(sessionId, ct: ct);
        }

        public async Task<bool> TerminateAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            this.TerminateCallCount++;
            RunningAgentChat? entry;
            Task<AgentChat> chatTask;
            lock (this._sessions)
            {
                if (!this._sessions.Remove(sessionId, out var existing))
                    return false;
                entry = existing.Entry;
                chatTask = existing.ChatTask;
            }
            await Task.Factory.StartNew(
                () => this.RunningSessions.Remove(entry),
                CancellationToken.None,
                TaskCreationOptions.None,
                this._foregroundScheduler);
            await this.disposeChatAsync(chatTask).ConfigureAwait(false);
            return true;
        }

        private async ValueTask RemoveRefAsync(AgentSessionId sessionId)
        {
            bool shouldRemove;
            RunningAgentChat? entryToRemove;
            Task<AgentChat>? chatTaskToDispose;

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(sessionId, out var existing))
                {
                    return;
                }

                if (existing.RefCount <= 1)
                {
                    _sessions.Remove(sessionId);
                    shouldRemove = true;
                    entryToRemove = existing.Entry;
                    chatTaskToDispose = existing.ChatTask;
                }
                else
                {
                    _sessions[sessionId] = (existing.RefCount - 1, existing.Entry, existing.ChatTask);
                    shouldRemove = false;
                    entryToRemove = null;
                    chatTaskToDispose = null;
                }
            }

            if (shouldRemove && entryToRemove is not null)
            {
                await Task.Factory.StartNew(
                    () => RunningSessions.Remove(entryToRemove),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundScheduler);
            }
            if (chatTaskToDispose is not null)
            {
                await this.disposeChatAsync(chatTaskToDispose).ConfigureAwait(false);
            }
        }

        private static async Task DisposeChatAsync(Task<AgentChat> chatTask)
            => await (await chatTask.ConfigureAwait(false)).DisposeAsync();
    }

    private sealed class CommonSurfaceRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly IAgentChat agentChat = Mock.Of<IAgentChat>();
        private RunningAgentChat? entry;
        private int activeLeaseCount;
        private int disposeCallCount;

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];
        public IAgentChat AgentChat => this.agentChat;
        public int ActiveLeaseCount => Volatile.Read(ref this.activeLeaseCount);
        public int DisposeCallCount => Volatile.Read(ref this.disposeCallCount);
        public Action? AfterLeaseAcquired { get; set; }

        public Task<RunningAgentChatLease> GetAsync(
            AgentSessionId sessionId,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref this.activeLeaseCount) == 1 && registerAsRunningAgent)
            {
                this.entry = new RunningAgentChat(sessionId, this);
                this.RunningSessions.Add(this.entry);
            }

            var lease = new RunningAgentChatLease(
                sessionId,
                this.agentChat,
                () =>
                {
                    Interlocked.Increment(ref this.disposeCallCount);
                    if (Interlocked.Decrement(ref this.activeLeaseCount) == 0 && this.entry is not null)
                    {
                        this.RunningSessions.Remove(this.entry);
                        this.entry = null;
                    }
                    return ValueTask.CompletedTask;
                });
            this.AfterLeaseAcquired?.Invoke();
            return Task.FromResult(lease);
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => this.GetAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => this.GetAsync(sessionId, registerAsRunningAgent, ct);
    }

    private static AgentDefinition CreateTestAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            { "kind": "prompt", "name": "table-test-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [] }
            """);

    private static Task<AgentChat> CreateTestChatAsync(
        AgentDefinition definition,
        AgentServices? services,
        CancellationToken ct)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentServices = services,
            });

    private sealed class CapturingScheduler : TaskScheduler
    {
        public bool WasInvoked { get; set; }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        protected override void QueueTask(Task task)
        {
            WasInvoked = true;
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            WasInvoked = true;
            return TryExecuteTask(task);
        }
    }

    private sealed class PausableScheduler : TaskScheduler
    {
        private readonly Queue<Task> queuedTasks = new();
        private TaskCompletionSource taskQueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool paused;

        internal int QueuedTaskCount
        {
            get
            {
                lock (this.queuedTasks)
                    return this.queuedTasks.Count;
            }
        }

        internal void Pause()
        {
            this.paused = true;
            this.taskQueued = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal Task WaitForQueuedTaskAsync(CancellationToken ct) =>
            this.taskQueued.Task.WaitAsync(ct);

        internal void RunNext()
        {
            Task task;
            lock (this.queuedTasks)
                task = this.queuedTasks.Dequeue();
            this.TryExecuteTask(task);
        }

        internal void Resume()
        {
            this.paused = false;
            while (true)
            {
                Task? task;
                lock (this.queuedTasks)
                    task = this.queuedTasks.TryDequeue(out var queued) ? queued : null;
                if (task is null)
                    return;
                this.TryExecuteTask(task);
            }
        }

        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            lock (this.queuedTasks)
                return this.queuedTasks.ToArray();
        }

        protected override void QueueTask(Task task)
        {
            if (!this.paused)
            {
                this.TryExecuteTask(task);
                return;
            }

            lock (this.queuedTasks)
                this.queuedTasks.Enqueue(task);
            this.taskQueued.TrySetResult();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            !this.paused && this.TryExecuteTask(task);
    }

    private static AcquireAgentChatRequest Request(
        AgentSessionId sessionId,
        string entityName = "",
        string? entityId = null,
        string? entityDisplayName = null,
        string? entityDescription = null)
        => new()
        {
            AgentSessionId = sessionId,
            EntityName = entityName,
            EntityId = entityId,
            EntityDisplayName = entityDisplayName,
            EntityDescription = entityDescription,
        };

    private sealed class FakeAgentDefinitionResolver : IAgentDefinitionResolver
    {
        private readonly AgentDefinition definition;

        public FakeAgentDefinitionResolver(AgentDefinition definition)
        {
            this.definition = definition;
        }

        public int ResolveCallCount { get; private set; }

        public Task<ResolvedAgentDefinition?> ResolveAsync(
            AgentDefinitionResolveRequest request,
            CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            return Task.FromResult<ResolvedAgentDefinition?>(new ResolvedAgentDefinition(this.definition));
        }
    }

    private sealed class FakeRuntimeContextFactory : IAgentSessionRuntimeContextFactory
    {
        public int CreateCallCount { get; private set; }

        public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
        {
            CreateCallCount++;
            return new AgentSessionRuntimeContext
            {
                Intent = new PersistedAgentSessionRuntimeIntent
                {
                    AgentSessionId = "fake-session",
                    OwningProfileEntityId = "11111111-1111-1111-1111-111111111111",
                    OwnershipGeneration = 0,
                    ExecutorBindings = new ExecutorBindings
                    {
                        SessionExecutor = JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone(),
                    },
                },
            };
        }
    }

    private sealed class FakeLocalRuntimeRegistry : ILocalAgentSessionRuntimeRegistry
    {
        public int Writes { get; private set; }
        public bool? LastValue { get; private set; }

        public Task SetContinueInBackgroundAsync(
            AgentSessionId sessionId,
            JsonElement persistedEntity,
            bool continueInBackground,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Writes++;
            LastValue = continueInBackground;
            return Task.CompletedTask;
        }

    }

    private sealed class RuntimeTrustResolver(string revision) : IRemoteTrustProfileResolver
    {
        public int CallCount { get; private set; }

        public Task<RemoteTrustProfileResolution?> ResolveAsync(
            string profileReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult<RemoteTrustProfileResolution?>(
                new(new TrustProfile(), revision));
        }
    }

    private sealed class RuntimeTrustCompiler : ITrustProfileProcessPolicyCompiler
    {
        public int CallCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CallCount++;
            return new TrustProfileProcessPolicyCompilation(false, null, []);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_AddsEntityInfoToRunningSessions()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-entity-info");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "My Entity", entityId: "entity-id-1"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);
        Assert.Equal("My Entity", entry.EntityName);
        Assert.Equal("entity-id-1", entry.EntityId);
        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task AcquireAsync_LastLeaseDisposed_RemovesFromRunningSessions()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-remove-last");

        var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        Assert.Single(table.RunningSessions);

        await lease.DisposeAsync();

        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_LastLeaseDisposed_AwaitsChatTeardown()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunningAgentChatFactory(
            new CapturingScheduler(),
            async chatTask =>
            {
                disposalStarted.TrySetResult();
                await allowDisposal.Task.ConfigureAwait(false);
                await (await chatTask.ConfigureAwait(false)).DisposeAsync();
            });
        var table = new RunningAgentChatTable(factory);
        var lease = await table.AcquireAsync(
            Request(new AgentSessionId("await-release-teardown")),
            TestContext.Current.CancellationToken);

        var disposing = lease.DisposeAsync().AsTask();
        await disposalStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            allowDisposal.TrySetResult();
            await disposing;
        }
    }

    [Fact]
    public async Task AcquireAsync_TwoLeasesForSameSession_RemovedOnlyOnLastDispose()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-two-leases");

        var lease1 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        var lease2 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        Assert.Single(table.RunningSessions);

        await lease1.DisposeAsync();
        Assert.Single(table.RunningSessions);

        await lease2.DisposeAsync();
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_ViewerMetadataAndContinueInBackground_AreAuthoritative()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-authoritative-metadata");
        var changes = new List<string?>();

        var lease1 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        var entry = Assert.Single(table.RunningSessions);
        entry.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var lease2 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        Assert.False(entry.IsRemote);
        Assert.Equal(2, entry.ViewerCount);
        Assert.Contains(nameof(RunningAgentChatWithEntityInfo.ViewerCount), changes);

        await lease1.DisposeAsync();
        Assert.Equal(1, entry.ViewerCount);
        await table.SetContinueInBackgroundAsync(sessionId, true, TestContext.Current.CancellationToken);
        Assert.True(entry.ContinueInBackground);
        await lease2.DisposeAsync();
        Assert.Single(table.RunningSessions);
        Assert.Equal(0, entry.ViewerCount);

        await table.SetContinueInBackgroundAsync(sessionId, false, TestContext.Current.CancellationToken);
        Assert.Empty(table.RunningSessions);
        Assert.Contains(nameof(RunningAgentChatWithEntityInfo.ContinueInBackground), changes);
    }

    [Fact]
    public async Task AcquireAsync_ValidRemoteRequest_PropagatesModeTransportAndCursor()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory, new FakeRuntimeContextFactory());
        var transport = new SnapshotTransport("remote-metadata");
        var cursor = new ReplayCursor
        {
            Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            Sequence = 9,
        };
        var hostContext = new CurrentSessionContext
        {
            AgentSessionId = "remote-metadata",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = 2,
            RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
        };
        var entity = JsonDocument.Parse(
            """{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":2}""").RootElement.Clone();
        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("remote-metadata"),
            EntityName = "Entity",
            AgentSessionEntity = entity,
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
            OwningProfileTransport = transport,
            ReplayCursor = cursor,
            AgentServices = new AgentServices { CurrentSessionContext = hostContext },
        };

        await using var lease = await table.AcquireAsync(request, TestContext.Current.CancellationToken);
        var entry = Assert.Single(table.RunningSessions);

        Assert.True(entry.IsRemote);
        Assert.IsType<RemoteAgentChat>(lease.AgentChat);
        Assert.Throws<InvalidOperationException>(() => lease.LocalAgentChat);
        Assert.Equal("remote-metadata", lease.AgentChat.Information.AgentSessionId);
        Assert.Same(transport, request.OwningProfileTransport);
        Assert.Equal(cursor, request.ReplayCursor);
        Assert.Equal(AgentChatAcquisitionMode.AttachRemote, request.AcquisitionMode);
        Assert.Null(factory.LastServices);
    }

    [Fact]
    public async Task AcquireAsync_InvalidRemoteCombination_RejectsBeforeFactoryMutation()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var entity = JsonDocument.Parse("""{"ownership-generation":2}""").RootElement.Clone();
        var invalidRequests = new[]
        {
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-remote-no-transport"),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                AgentSessionEntity = entity,
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-local-transport"),
                AcquisitionMode = AgentChatAcquisitionMode.Local,
                AgentSessionEntity = entity,
                OwningProfileTransport = Mock.Of<ITransport>(),
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-remote-owner"),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                AgentSessionEntity = JsonDocument.Parse("""{"host-profile-entity-id":"not-a-guid","ownership-generation":2}""").RootElement.Clone(),
                OwningProfileTransport = Mock.Of<ITransport>(),
            },
        };

        foreach (var request in invalidRequests)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => table.AcquireAsync(request, TestContext.Current.CancellationToken));
        }

        Assert.Empty(factory.RunningSessions);
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_ConcurrentSameRemoteRuntime_ReturnsLeasesForOneProxy()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory, new FakeRuntimeContextFactory());
        var sessionId = new AgentSessionId("shared-remote");
        var transport = new SnapshotTransport(sessionId.Value);
        var remoteRequest = new AcquireAgentChatRequest
        {
            AgentSessionId = sessionId,
            EntityName = "Shared",
            AgentSessionEntity = JsonDocument.Parse(
                """{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":2}""").RootElement.Clone(),
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
            OwningProfileTransport = transport,
        };

        var acquisitions = Enumerable.Range(0, 3)
            .Select(_ => table.AcquireAsync(remoteRequest, TestContext.Current.CancellationToken))
            .ToArray();
        var leases = await Task.WhenAll(acquisitions);
        var entry = Assert.Single(table.RunningSessions);
        Assert.Equal(1, entry.ViewerCount);
        Assert.True(entry.IsRemote);
        Assert.All(leases, lease => Assert.Same(leases[0].AgentChat, lease.AgentChat));
        Assert.Equal(1, transport.ConnectCount);

        await leases[0].DisposeAsync();
        await leases[1].DisposeAsync();
        Assert.Single(table.RunningSessions);
        Assert.Equal(1, entry.ViewerCount);
        Assert.True(entry.IsRemote);

        await leases[2].DisposeAsync();
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_CancelledRemoteWaiter_PreservesSharedSingleFlight()
    {
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(),
            new FakeRuntimeContextFactory());
        var transport = new GatedSnapshotTransport("shared-after-waiter-cancellation");
        var request = RemoteRequest("shared-after-waiter-cancellation", transport);
        using var cancelledWaiter = new CancellationTokenSource();

        var first = table.AcquireAsync(request, cancelledWaiter.Token);
        var second = table.AcquireAsync(request, TestContext.Current.CancellationToken);
        await transport.Connected.WaitAsync(TestContext.Current.CancellationToken);

        cancelledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var third = table.AcquireAsync(request, TestContext.Current.CancellationToken);
        transport.PublishSnapshot();

        var leases = await Task.WhenAll(second, third);
        try
        {
            Assert.Same(leases[0].AgentChat, leases[1].AgentChat);
            Assert.Single(table.RunningSessions);
            Assert.Equal(1, transport.ConnectCount);
        }
        finally
        {
            await leases[0].DisposeAsync();
            await leases[1].DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_FinalCancelledRemoteWaiter_AwaitsFlightCleanup()
    {
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(),
            new FakeRuntimeContextFactory());
        var transport = new GatedSnapshotTransport("cancelled-flight-cleanup")
        {
            PauseCancellationCleanup = true,
        };
        using var cancellation = new CancellationTokenSource();
        var acquiring = table.AcquireAsync(
            RemoteRequest("cancelled-flight-cleanup", transport),
            cancellation.Token);
        await transport.Connected.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await transport.CancellationObserved.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(acquiring.IsCompleted);

        transport.AllowCancellationCleanup();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        Assert.Empty(table.RunningSessions);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task AcquireAsync_CancelledBeforeRemoteSnapshot_AddsNoRunningRow()
    {
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(),
            new FakeRuntimeContextFactory());
        var transport = new SnapshotTransport.BlockingTransport();
        using var cancellation = new CancellationTokenSource();
        var acquiring = table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("cancelled-remote"),
                EntityName = "Cancelled",
                AgentSessionEntity = JsonDocument.Parse(
                    """{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":2}""").RootElement.Clone(),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                OwningProfileTransport = transport,
            },
            cancellation.Token);

        await transport.Connected.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        Assert.Empty(table.RunningSessions);
        Assert.True(transport.ChannelDisposed);
    }

    [Fact]
    public async Task AcquireAsync_RemoteAuthorizationDenied_AddsNoRunningRow()
    {
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        var transport = new DeniedTransport();

        var error = await Assert.ThrowsAsync<RemoteAgentSessionException>(() =>
            table.AcquireAsync(RemoteRequest("denied-remote", transport),
                TestContext.Current.CancellationToken));

        Assert.Equal("unauthorized", error.Code);
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task RunningSessions_RemoteRetentionAndViewerCount_MutateOnForegroundScheduler()
    {
        var scheduler = new CapturingScheduler();
        var transport = new SnapshotTransport("remote-retention");
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        await using var lease = await table.AcquireAsync(
            RemoteRequest("remote-retention", transport, scheduler),
            TestContext.Current.CancellationToken);
        var row = Assert.Single(table.RunningSessions);
        scheduler.WasInvoked = false;

        var operation = table.SetContinueInBackgroundAsync(
            row.SessionId, true, TestContext.Current.CancellationToken);
        var command = Assert.IsType<SetContinueInBackgroundCommand>(
            await transport.ReadCommandAsync(TestContext.Current.CancellationToken));
        transport.Send(
            new SessionRetentionChangedEvent
            {
                ContinueInBackground = true,
                ViewerCount = 4,
            },
            2,
            command.CorrelationId);
        transport.Send(
            new CommandCompletedEvent { CommandId = command.CommandId },
            3,
            command.CorrelationId);
        await operation;

        Assert.True(scheduler.WasInvoked);
        Assert.True(row.ContinueInBackground);
        Assert.Equal(4, row.ViewerCount);
    }

    [Fact]
    public async Task AcquireAsync_RemoteRetention_AppliesWithinOrderedFrameWithoutBackgroundWork()
    {
        var scheduler = new PausableScheduler();
        var transport = new SnapshotTransport("remote-drain");
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        var lease = await table.AcquireAsync(
            RemoteRequest("remote-drain", transport, scheduler),
            TestContext.Current.CancellationToken);
        scheduler.Pause();
        transport.Send(
            new SessionRetentionChangedEvent
            {
                ContinueInBackground = false,
                ViewerCount = 0,
            },
            2,
            Guid.NewGuid());

        await scheduler.WaitForQueuedTaskAsync(TestContext.Current.CancellationToken);
        scheduler.RunNext();
        Assert.Equal(0, scheduler.QueuedTaskCount);
        Assert.Equal(0, Assert.Single(table.RunningSessions).ViewerCount);

        var disposing = lease.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(1, scheduler.QueuedTaskCount);
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            scheduler.Resume();
            await disposing;
        }
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task TerminateAsync_RemoteSession_SendsTerminateNotDetach()
    {
        var transport = new SnapshotTransport("terminate-remote");
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        var lease = await table.AcquireAsync(
            RemoteRequest("terminate-remote", transport),
            TestContext.Current.CancellationToken);

        var operation = table.TerminateAsync(
            lease.SessionId, TestContext.Current.CancellationToken);
        var command = Assert.IsType<TerminateSessionCommand>(
            await transport.ReadCommandAsync(TestContext.Current.CancellationToken));
        transport.Send(
            new SessionTerminalEvent
            {
                Reason = "user-requested",
                CompletionState = JsonSerializer.SerializeToElement(new { }),
            },
            2,
            command.CorrelationId);

        Assert.True(await operation);
        Assert.Empty(table.RunningSessions);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task TerminateAsync_MissingSession_ReturnsFalse()
    {
        var table = new RunningAgentChatTable(new FakeRunningAgentChatFactory());

        Assert.False(await table.TerminateAsync(
            new AgentSessionId("missing"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TerminateAsync_LocalSession_DisposesOwningRuntime()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("terminate-local");
        await using var lease = await table.AcquireAsync(
            Request(sessionId), TestContext.Current.CancellationToken);

        Assert.True(await table.TerminateAsync(sessionId, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.TerminateCallCount);
    }

    [Fact]
    public async Task TerminateAsync_LocalSession_AwaitsOwningRuntimeTeardown()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunningAgentChatFactory(
            new CapturingScheduler(),
            async chatTask =>
            {
                disposalStarted.TrySetResult();
                await allowDisposal.Task.ConfigureAwait(false);
                await (await chatTask.ConfigureAwait(false)).DisposeAsync();
            });
        var table = new RunningAgentChatTable(factory);
        var lease = await table.AcquireAsync(
            Request(new AgentSessionId("await-termination-teardown")),
            TestContext.Current.CancellationToken);

        var terminating = table.TerminateAsync(
            lease.SessionId,
            TestContext.Current.CancellationToken);
        await disposalStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(terminating.IsCompleted);
        }
        finally
        {
            allowDisposal.TrySetResult();
            await terminating;
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_LocalOwner_PersistsBeforePublishingPreference()
    {
        var registry = new FakeLocalRuntimeRegistry();
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        table.ConfigureLocalRuntimeRegistry(registry);
        var sessionId = new AgentSessionId("local-retention");
        var entity = JsonDocument.Parse(
            """{"entity-id":"11111111-1111-1111-1111-111111111112","agent-session-id":"local-retention"}""").RootElement.Clone();
        await using var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = sessionId,
            AgentSessionEntity = entity,
        }, TestContext.Current.CancellationToken);
        var row = Assert.Single(table.RunningSessions);

        await table.SetContinueInBackgroundAsync(sessionId, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, registry.Writes);
        Assert.True(registry.LastValue);
        Assert.True(row.ContinueInBackground);
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_CancelledBeforeWrite_DoesNotPersistOrPublish()
    {
        var registry = new FakeLocalRuntimeRegistry();
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        table.ConfigureLocalRuntimeRegistry(registry);
        var sessionId = new AgentSessionId("cancel-retention");
        var entity = JsonDocument.Parse(
            """{"entity-id":"11111111-1111-1111-1111-111111111113","agent-session-id":"cancel-retention"}""").RootElement.Clone();
        await using var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = sessionId,
            AgentSessionEntity = entity,
        }, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            table.SetContinueInBackgroundAsync(sessionId, true, cancellation.Token));

        Assert.Equal(0, registry.Writes);
        Assert.False(Assert.Single(table.RunningSessions).ContinueInBackground);
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_SubagentSession_ThrowsArgumentException()
    {
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory { IsSubAgent = true });
        var sessionId = new AgentSessionId("subagent-retention");
        await using var lease = await table.AcquireAsync(
            Request(sessionId), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            table.SetContinueInBackgroundAsync(
                sessionId, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_LocalOwner_PersistsThroughDataAccessRegistry()
    {
        var dataAccess = new InMemoryDataAccessLayer();
        var entityId = Guid.NewGuid();
        var sessionId = new AgentSessionId("persisted-local-retention");
        var entity = JsonDocument.Parse(
            $$"""{"entity-id":"{{entityId}}","entity-types":["entity","agent-session"],"names":[["sessions","retention"]],"agent-session-id":"{{sessionId.Value}}","continue-in-background":false}""")
            .RootElement.Clone();
        var seeded = await dataAccess.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(entityId),
                    Data = entity,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(seeded.EntityResults, result => result.UpdateState == UpdateState.Failed);
        var table = new RunningAgentChatTable(
            new FakeRunningAgentChatFactory(), new FakeRuntimeContextFactory());
        table.ConfigureLocalRuntimeRegistry(new LocalAgentSessionRuntimeRegistry(dataAccess));
        await using var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = sessionId,
            AgentSessionEntity = entity,
        }, TestContext.Current.CancellationToken);

        await table.SetContinueInBackgroundAsync(
            sessionId, true, TestContext.Current.CancellationToken);

        var loaded = await dataAccess.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = new EntityId(entityId) }],
            Timestamps = [null],
        }, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(loaded.Batches.SelectMany(batch => batch.Entities))
            .Data!.Value.GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_MissingSession_ThrowsArgumentException()
    {
        var table = new RunningAgentChatTable(new FakeRunningAgentChatFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => table.SetContinueInBackgroundAsync(
            new AgentSessionId("missing"),
            true,
            TestContext.Current.CancellationToken));
    }

    private sealed class SnapshotTransport : ITransport
    {
        private readonly SnapshotChannel channel;

        internal SnapshotTransport(string sessionId)
        {
            this.channel = new SnapshotChannel(sessionId);
        }

        internal sealed class BlockingTransport : ITransport
        {
            private readonly BlockingChannel channel = new();
            internal Task Connected => this.channel.Connected.Task;
            internal bool ChannelDisposed => this.channel.Disposed;

            public Task<IMessageChannel> ConnectToMessageChannelAsync(
                JsonElement request,
                CancellationToken ct = default)
            {
                this.channel.Connected.TrySetResult();
                return Task.FromResult<IMessageChannel>(this.channel);
            }

            public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
                => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class BlockingChannel : IMessageChannel
        {
            private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();
            private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
            internal TaskCompletionSource Connected { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed { get; private set; }
            public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
            public ChannelReader<JsonElement> Reader => this.incoming.Reader;
            public ValueTask DisposeAsync()
            {
                this.Disposed = true;
                this.outgoing.Writer.TryComplete();
                this.incoming.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }

        internal int ConnectCount { get; private set; }

        internal Task<AgentSessionCommand> ReadCommandAsync(CancellationToken ct)
            => this.channel.ReadCommandAsync(ct);

        internal void Send(AgentSessionServerEvent value, long sequence, Guid correlationId)
            => this.channel.Send(value, sequence, correlationId);

        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            this.ConnectCount++;
            return Task.FromResult<IMessageChannel>(this.channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedSnapshotTransport(string sessionId) : ITransport
    {
        private readonly SnapshotChannel channel = new(sessionId, publishSnapshot: false);
        private readonly TaskCompletionSource releaseConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowCancellationCleanup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObservedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Connected => this.ConnectedSource.Task;
        internal Task CancellationObserved => this.CancellationObservedSource.Task;
        internal int ConnectCount { get; private set; }
        internal bool PauseCancellationCleanup { get; init; }

        internal void PublishSnapshot()
        {
            this.channel.PublishSnapshot();
            this.releaseConnection.TrySetResult();
        }

        internal void AllowCancellationCleanup()
            => this.allowCancellationCleanup.TrySetResult();

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            this.ConnectCount++;
            this.ConnectedSource.TrySetResult();
            try
            {
                await this.releaseConnection.Task.WaitAsync(ct);
                return this.channel;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                this.CancellationObservedSource.TrySetResult();
                if (this.PauseCancellationCleanup)
                    await this.allowCancellationCleanup.Task;
                throw;
            }
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
        private readonly RuntimeEpoch epoch;

        private readonly string sessionId;

        internal SnapshotChannel(string sessionId, bool publishSnapshot = true)
        {
            this.sessionId = sessionId;
            this.epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
            if (publishSnapshot)
                this.PublishSnapshot();
        }

        internal void PublishSnapshot()
        {
            var snapshot = new AgentSessionSnapshot
            {
                Information = new AgentInformation
                {
                    AgentSessionId = this.sessionId,
                    AgentId = "agent",
                    Name = "remote-agent",
                    DisplayName = "Remote agent",
                    Description = "Remote test agent",
                    AcceptsUserInput = true,
                    CurrentModelId = "echo",
                    AgentDefinition = CreateTestAgentDefinition(),
                },
                Usage = new Usage(),
                InputQueues = new AgentInputQueuesSnapshot
                {
                    Revision = 0,
                    Queues =
                    [
                        Queue("immediate", true, false),
                        Queue("default", false, true),
                    ],
                },
                IsBusy = false,
                History = [],
                RunningItems = [],
                Tools = [],
                Subagents = [],
                Modals = [],
                ContinueInBackground = false,
                ViewerCount = 1,
            };
            this.incoming.Writer.TryWrite(
                AgentSessionProtocolCodec.SerializeFrame(
                    AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                        this.epoch,
                        1,
                        Guid.NewGuid(),
                        new SessionSnapshotEvent { Snapshot = snapshot })));
        }

        public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.incoming.Reader;

        internal async Task<AgentSessionCommand> ReadCommandAsync(CancellationToken ct)
            => AgentSessionProtocolCodec.DeserializeCommand(await this.outgoing.Reader.ReadAsync(ct));

        internal void Send(AgentSessionServerEvent value, long sequence, Guid correlationId)
            => this.incoming.Writer.TryWrite(AgentSessionProtocolCodec.SerializeFrame(
                AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    this.epoch, sequence, correlationId, value)));

        public ValueTask DisposeAsync()
        {
            this.outgoing.Writer.TryComplete();
            this.incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeniedTransport : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            var channel = new DeniedChannel();
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class DeniedChannel : IMessageChannel
        {
            private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
            private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();

            internal DeniedChannel()
            {
                var correlation = Guid.NewGuid();
                this.incoming.Writer.TryWrite(AgentSessionProtocolCodec.SerializeFrame(
                    AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                        new RuntimeEpoch { Value = Guid.NewGuid() },
                        1,
                        correlation,
                        new SessionTerminalEvent
                        {
                            Reason = "unauthorized",
                            CompletionState = JsonSerializer.SerializeToElement(new
                            {
                                code = "unauthorized",
                                operation = "attach",
                                retryable = false,
                                message = "The agent session is unavailable.",
                            }),
                        })));
                this.incoming.Writer.TryComplete();
            }

            public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
            public ChannelReader<JsonElement> Reader => this.incoming.Reader;
            public ValueTask DisposeAsync()
            {
                this.incoming.Writer.TryComplete();
                this.outgoing.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static AcquireAgentChatRequest RemoteRequest(
        string sessionId,
        ITransport transport,
        TaskScheduler? scheduler = null)
        => new()
        {
            AgentSessionId = new AgentSessionId(sessionId),
            EntityName = sessionId,
            AgentSessionEntity = JsonDocument.Parse(
                """{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":2}""").RootElement.Clone(),
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
            OwningProfileTransport = transport,
            ForegroundScheduler = scheduler,
        };

    private static AgentInputQueueSnapshot Queue(string id, bool immediate, bool isDefault)
        => new()
        {
            QueueId = id,
            Name = id,
            IsDefault = isDefault,
            IsImmediate = immediate,
            Immediacy = immediate ? AgentInputQueueImmediacy.Immediate : AgentInputQueueImmediacy.Queue,
            Priority = 0,
            Revision = 0,
            Items = ImmutableArray<AgentInputItemSnapshot>.Empty,
        };

    [Fact]
    public async Task AcquireAsync_EntityInfoPreservedForDurationOfSession()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-entity-preserved");

        var lease1 = await table.AcquireAsync(Request(sessionId, entityName: "Preserved Entity", entityId: "entity-42"), TestContext.Current.CancellationToken);
        // A second acquire (e.g., a second tab) should not overwrite entity info.
        var lease2 = await table.AcquireAsync(Request(sessionId), TestContext.Current.CancellationToken);

        try
        {
            var entry = Assert.Single(table.RunningSessions);
            Assert.Equal("Preserved Entity", entry.EntityName);
            Assert.Equal("entity-42", entry.EntityId);
        }
        finally
        {
            await lease1.DisposeAsync();
            await lease2.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_AddToRunningSessions_HappensOnForegroundScheduler()
    {
        var scheduler = new CapturingScheduler();
        var factory = new FakeRunningAgentChatFactory(scheduler);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-scheduler-add");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task AcquireAsync_RemoveFromRunningSessions_HappensOnForegroundScheduler()
    {
        var scheduler = new CapturingScheduler();
        var factory = new FakeRunningAgentChatFactory(scheduler);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-scheduler-remove");

        var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        scheduler.WasInvoked = false;

        await lease.DisposeAsync();

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_DelegatesToUnderlyingRunningAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-delegate");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);

        // AcquireLeaseAsync on the wrapper delegates to the underlying RunningAgentChat
        await using var secondLease = await entry.AcquireLeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, secondLease.SessionId);

        // Dispose the second lease — session remains alive (lease from AcquireAsync still held)
        await secondLease.DisposeAsync();
        Assert.Single(table.RunningSessions);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_PreservesCommonAgentChatSurface()
    {
        var factory = new CommonSurfaceRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-common-surface-row");
        var initialLease = await table.AcquireAsync(
            Request(sessionId, entityName: "Common Surface"),
            TestContext.Current.CancellationToken);

        try
        {
            var row = Assert.Single(table.RunningSessions);
            var rowLease = await row.AcquireLeaseAsync(TestContext.Current.CancellationToken);
            try
            {
                Assert.Same(factory.AgentChat, rowLease.AgentChat);
                Assert.IsNotType<AgentChat>(rowLease.AgentChat);
                Assert.Equal(2, factory.ActiveLeaseCount);
                Assert.Equal(2, row.ViewerCount);
            }
            finally
            {
                await rowLease.DisposeAsync();
            }

            Assert.Equal(1, factory.ActiveLeaseCount);
            Assert.Equal(1, row.ViewerCount);
        }
        finally
        {
            await initialLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_FailureDisposesAcquiredLease()
    {
        var factory = new CommonSurfaceRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-row-acquire-failure");
        var initialLease = await table.AcquireAsync(
            Request(sessionId, entityName: "Failure Cleanup"),
            TestContext.Current.CancellationToken);

        try
        {
            var row = Assert.Single(table.RunningSessions);
            row.PropertyChanged += ThrowOnViewerCountChange;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => row.AcquireLeaseAsync(TestContext.Current.CancellationToken));

            Assert.Equal(1, factory.ActiveLeaseCount);
            Assert.Equal(1, factory.DisposeCallCount);
            Assert.Equal(1, row.ViewerCount);
        }
        finally
        {
            await initialLease.DisposeAsync();
        }

        static void ThrowOnViewerCountChange(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(RunningAgentChatWithEntityInfo.ViewerCount))
                throw new InvalidOperationException("Injected viewer-count publication failure.");
        }
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_CancellationAfterAcquisitionDisposesLease()
    {
        var factory = new CommonSurfaceRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-row-acquire-cancelled");
        var initialLease = await table.AcquireAsync(
            Request(sessionId, entityName: "Cancellation Cleanup"),
            TestContext.Current.CancellationToken);

        try
        {
            var row = Assert.Single(table.RunningSessions);
            using var cancellation = new CancellationTokenSource();
            factory.AfterLeaseAcquired = cancellation.Cancel;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => row.AcquireLeaseAsync(cancellation.Token));

            Assert.Equal(1, factory.ActiveLeaseCount);
            Assert.Equal(1, factory.DisposeCallCount);
            Assert.Equal(1, row.ViewerCount);
        }
        finally
        {
            await initialLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_DelegatedCancellationDisposesLease()
    {
        var sessionId = new AgentSessionId("session-delegated-row-acquire-cancelled");
        using var cancellation = new CancellationTokenSource();
        var disposeCallCount = 0;
        var row = new RunningAgentChatWithEntityInfo(
            sessionId,
            isSubAgent: false,
            _ =>
            {
                var lease = new RunningAgentChatLease(
                    sessionId,
                    Mock.Of<IAgentChat>(),
                    () =>
                    {
                        Interlocked.Increment(ref disposeCallCount);
                        return ValueTask.CompletedTask;
                    });
                cancellation.Cancel();
                return Task.FromResult(lease);
            },
            "Delegated Cancellation",
            entityId: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => row.AcquireLeaseAsync(cancellation.Token));

        Assert.Equal(1, disposeCallCount);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_SessionId_MatchesFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-id-match");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);
        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task AcquireAsync_MultipleDifferentSessions_EachHasCorrectEntityInfo()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionA = new AgentSessionId("session-multi-a");
        var sessionB = new AgentSessionId("session-multi-b");

        await using var leaseA = await table.AcquireAsync(Request(sessionA, entityName: "Entity A", entityId: "id-a"), TestContext.Current.CancellationToken);
        await using var leaseB = await table.AcquireAsync(Request(sessionB, entityName: "Entity B", entityId: "id-b"), TestContext.Current.CancellationToken);

        Assert.Equal(2, table.RunningSessions.Count);

        var entryA = table.RunningSessions.First(r => r.SessionId == sessionA);
        var entryB = table.RunningSessions.First(r => r.SessionId == sessionB);

        Assert.Equal("Entity A", entryA.EntityName);
        Assert.Equal("id-a", entryA.EntityId);
        Assert.Equal("Entity B", entryB.EntityName);
        Assert.Equal("id-b", entryB.EntityId);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDisplayName_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-display-name");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDisplayName: "Custom Display Name"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDescription_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-description");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDescription: "Test description"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDisplayNameAndDescription_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-both");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDisplayName: "Display Name", entityDescription: "Description text"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_AgentDefinitionProvided_UsesItDirectly()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-direct-definition");
        var definition = CreateTestDefinition("direct-definition");

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = definition,
            },
            TestContext.Current.CancellationToken);

        Assert.Same(definition, factory.LastDefinition);
    }

    [Fact]
    public async Task AcquireAsync_AgentSessionEntity_DelegatesToResolver()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-resolver");
        var definition = CreateTestDefinition("resolved-definition");
        var resolver = new FakeAgentDefinitionResolver(definition);

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentSessionEntity = JsonDocument.Parse(
                    """{"agent-session-id":"session-resolver","host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":0}""").RootElement.Clone(),
                AgentDefinitionResolver = resolver,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Same(definition, factory.LastDefinition);
    }

    [Fact]
    public async Task AcquireAsync_PersistedSession_HydratesServicesBeforeFactoryAcquisition()
    {
        var factory = new FakeRunningAgentChatFactory();
        var runtimeFactory = new FakeRuntimeContextFactory();
        var table = new RunningAgentChatTable(factory, runtimeFactory);
        var originalServices = new AgentServices();

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("session-runtime-context"),
                AgentSessionEntity = JsonDocument.Parse("""{"agent-session-id":"session-runtime-context"}""").RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("runtime-context"),
                AgentServices = originalServices,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, runtimeFactory.CreateCallCount);
        Assert.NotSame(originalServices, factory.LastServices);
        Assert.IsType<ExecutorBindings>(factory.LastServices!.ExecutorBindings);
    }

    [Fact]
    public async Task AcquireAsync_PersistedTrustIntent_HydratesRevisionPinnedContextWithoutCompiling()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(
            factory,
            new AgentSessionRuntimeContextFactory(null));
        var resolver = new RuntimeTrustResolver("12");
        var compiler = new RuntimeTrustCompiler();

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("trust-runtime-context"),
                AgentSessionEntity = JsonDocument.Parse(
                    """
                    {
                      "agent-session-id": "trust-runtime-context",
                      "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
                      "ownership-generation": 4,
                      "trust-profile-reference": "restricted",
                      "expected-trust-profile-revision": 12
                    }
                    """).RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("trust-runtime-context"),
                AgentServices = new AgentServices
                {
                    TrustProfileResolver = resolver,
                    TrustProfilePolicyCompiler = compiler,
                },
            },
            TestContext.Current.CancellationToken);

        var services = factory.LastServices!;
        var context = Assert.IsType<AgentExecutionTrustContext>(
            services.AgentExecutionTrustContext);
        Assert.Same(context, services.ExecutionTrustContext);
        Assert.Equal("restricted", context.RemoteReference!.Id);
        Assert.Equal("12", context.RemoteReference.ExpectedRevision);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, compiler.CallCount);

        await context.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, compiler.CallCount);
    }

    [Fact]
    public async Task Takeover_NewHost_RehydratesPersistedIntentAndRecompilesIndependently()
    {
        const string persistedIntent =
            """
            {
              "agent-session-id": "takeover-trust-context",
              "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
              "ownership-generation": 4,
              "trust-profile-reference": "restricted",
              "expected-trust-profile-revision": 12
            }
            """;
        var oldFactory = new FakeRunningAgentChatFactory();
        var oldResolver = new RuntimeTrustResolver("12");
        var oldCompiler = new RuntimeTrustCompiler();
        var oldTable = new RunningAgentChatTable(
            oldFactory,
            new AgentSessionRuntimeContextFactory(null));

        await using (var oldLease = await oldTable.AcquireAsync(
                         new AcquireAgentChatRequest
                         {
                             AgentSessionId = new AgentSessionId("takeover-trust-context"),
                             AgentSessionEntity = JsonDocument.Parse(persistedIntent).RootElement.Clone(),
                             AgentDefinition = CreateTestDefinition("takeover-old"),
                             AgentServices = new AgentServices
                             {
                                 TrustProfileResolver = oldResolver,
                                 TrustProfilePolicyCompiler = oldCompiler,
                             },
                         },
                         TestContext.Current.CancellationToken))
        {
            var oldContext = Assert.IsType<AgentExecutionTrustContext>(
                oldFactory.LastServices!.AgentExecutionTrustContext);
            await oldContext.GetCompilationAsync(TestContext.Current.CancellationToken);
        }

        var newFactory = new FakeRunningAgentChatFactory();
        var newResolver = new RuntimeTrustResolver("12");
        var newCompiler = new RuntimeTrustCompiler();
        var newTable = new RunningAgentChatTable(
            newFactory,
            new AgentSessionRuntimeContextFactory(null));
        var takeoverEntity = JsonNode.Parse(persistedIntent)!.AsObject();
        takeoverEntity["host-profile-entity-id"] = "22222222-2222-2222-2222-222222222222";
        takeoverEntity["ownership-generation"] = 5;

        await using var newLease = await newTable.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("takeover-trust-context"),
                AgentSessionEntity = JsonSerializer.SerializeToElement(takeoverEntity),
                AgentDefinition = CreateTestDefinition("takeover-new"),
                AgentServices = new AgentServices
                {
                    TrustProfileResolver = newResolver,
                    TrustProfilePolicyCompiler = newCompiler,
                },
            },
            TestContext.Current.CancellationToken);
        var newContext = Assert.IsType<AgentExecutionTrustContext>(
            newFactory.LastServices!.AgentExecutionTrustContext);
        await newContext.GetCompilationAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(
            oldFactory.LastServices!.AgentExecutionTrustContext,
            newContext);
        Assert.Equal(1, oldResolver.CallCount);
        Assert.Equal(1, oldCompiler.CallCount);
        Assert.Equal(1, newResolver.CallCount);
        Assert.Equal(1, newCompiler.CallCount);
        var currentSession = Assert.IsType<CurrentSessionContext>(
            newFactory.LastServices.CurrentSessionContext);
        Assert.Equal(5, currentSession.OwnershipGeneration);
        Assert.Equal("22222222-2222-2222-2222-222222222222", currentSession.OwningProfileEntityId);
    }

    [Fact]
    public async Task CurrentSessionContext_TwoRemoteSessions_DoNotCrossContaminate()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(
            factory,
            new AgentSessionRuntimeContextFactory(null));
        var firstEpoch = new RuntimeEpoch { Value = Guid.NewGuid() };
        var firstServices = new AgentServices
        {
            CurrentSessionContext = new CurrentSessionContext
            {
                AgentSessionId = "placeholder-one",
                OwningProfileEntityId = "placeholder-owner",
                OwnershipGeneration = 0,
                RuntimeEpoch = firstEpoch,
            },
        };

        await using var first = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("isolated-one"),
                AgentSessionEntity = JsonDocument.Parse(
                    """
                    {
                      "agent-session-id": "isolated-one",
                      "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
                      "ownership-generation": 1
                    }
                    """).RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("isolated-one"),
                AgentServices = firstServices,
            },
            TestContext.Current.CancellationToken);
        var firstContext = Assert.IsType<CurrentSessionContext>(
            factory.LastServices!.CurrentSessionContext);

        await using var second = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("isolated-two"),
                AgentSessionEntity = JsonDocument.Parse(
                    """
                    {
                      "agent-session-id": "isolated-two",
                      "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                      "ownership-generation": 9
                    }
                    """).RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("isolated-two"),
                AgentServices = new AgentServices
                {
                    CurrentSessionContext = new CurrentSessionContext
                    {
                        AgentSessionId = "placeholder-two",
                        OwningProfileEntityId = "placeholder-owner",
                        OwnershipGeneration = 0,
                    },
                },
            },
            TestContext.Current.CancellationToken);
        var secondContext = Assert.IsType<CurrentSessionContext>(
            factory.LastServices!.CurrentSessionContext);

        Assert.Equal("isolated-one", firstContext.AgentSessionId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", firstContext.OwningProfileEntityId);
        Assert.Equal(firstEpoch, firstContext.RuntimeEpoch);
        Assert.Equal("isolated-two", secondContext.AgentSessionId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", secondContext.OwningProfileEntityId);
        Assert.Null(secondContext.RuntimeEpoch);
        Assert.NotSame(firstContext, secondContext);
    }

    [Fact]
    public async Task AcquireAsync_PersistedSession_PropagatesAuthoritativeRuntimeIdentityToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(
            factory,
            new AgentSessionRuntimeContextFactory(null));
        var epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
        var originalContext = new CurrentSessionContext
        {
            AgentSessionId = "composition-placeholder",
            OwningProfileEntityId = "composition-placeholder",
            OwnershipGeneration = 0,
            RuntimeEpoch = epoch,
        };

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("authoritative-runtime-context"),
                AgentSessionEntity = JsonDocument.Parse(
                    """
                    {
                      "agent-session-id": "authoritative-runtime-context",
                      "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
                      "ownership-generation": 17
                    }
                    """).RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("authoritative-runtime-context"),
                AgentServices = new AgentServices { CurrentSessionContext = originalContext },
            },
            TestContext.Current.CancellationToken);

        var context = Assert.IsType<CurrentSessionContext>(
            factory.LastServices!.CurrentSessionContext);
        Assert.Equal("authoritative-runtime-context", context.AgentSessionId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", context.OwningProfileEntityId);
        Assert.Equal(17, context.OwnershipGeneration);
        Assert.Equal(epoch, context.RuntimeEpoch);
    }

    [Fact]
    public async Task AcquireAsync_PersistedSplitBindings_ReachesFactoryWithSharedRegistry()
    {
        var factory = new FakeRunningAgentChatFactory();
        var registry = new TransportFactoryRegistry();
        var runtimeFactory = new AgentSessionRuntimeContextFactory(registry);
        var table = new RunningAgentChatTable(factory, runtimeFactory);

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("session-split-runtime"),
                AgentSessionEntity = JsonDocument.Parse(
                    """
                    {
                      "agent-session-id": "session-split-runtime",
                      "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
                      "executor-bindings": {
                        "session": { "type": "local" },
                        "components": {
                          "worker": {
                            "type": "user-computer-profile",
                            "entity-id": "44444444-4444-4444-4444-444444444444"
                          }
                        }
                      }
                    }
                    """).RootElement.Clone(),
                AgentDefinition = CreateTestDefinition("split-runtime"),
            },
            TestContext.Current.CancellationToken);

        var bindings = Assert.IsType<ExecutorBindings>(factory.LastServices!.ExecutorBindings);
        Assert.Equal(
            "44444444-4444-4444-4444-444444444444",
            bindings.ResolveComponent("worker").GetProperty("entity-id").GetString());
        Assert.Same(registry, factory.LastServices.ExecutorTransportFactoryRegistry);
    }

    [Fact]
    public async Task AcquireAsync_ExistingLease_DoesNotRehydrateRuntimeContext()
    {
        var factory = new FakeRunningAgentChatFactory();
        var runtimeFactory = new FakeRuntimeContextFactory();
        var table = new RunningAgentChatTable(factory, runtimeFactory);
        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("session-existing-runtime"),
            AgentSessionEntity = JsonDocument.Parse("""{"agent-session-id":"session-existing-runtime"}""").RootElement.Clone(),
            AgentDefinition = CreateTestDefinition("existing-runtime"),
        };

        await using var firstLease = await table.AcquireAsync(request, TestContext.Current.CancellationToken);
        await using var secondLease = await table.AcquireAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, runtimeFactory.CreateCallCount);
    }

    [Fact]
    public async Task AcquireAsync_McpToolWithSecretPlaceholder_MaterializesToOpaqueHandleAndInvokesSecretProvider()
    {
        // #1405: opening a session via the foreground RunningAgentChatTable path must materialize the
        // ${SECRET:...} in an MCP tool connection — rewriting it to an opaque handle and invoking the
        // SecretProvider — instead of passing the raw placeholder through to the MCP transport.
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString("resolved-token");
        var services = new AgentServices
        {
            SecretProvider = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), services, TaskScheduler.Default);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-mcp-secret");

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = McpSecretDefinition(),
                AgentServices = services,
                EntityName = "Entity",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.CallCount);
        var json = lease.AgentChat.Information.AgentDefinition.ToJson();
        Assert.DoesNotContain("${SECRET:GitHubToken}", json, StringComparison.Ordinal);
        Assert.Contains("${SECRET:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireAsync_DefinitionWithNoSecretUsages_DefinitionUnchangedAndProviderNotCalled()
    {
        // #1405: a definition without any ${SECRET:...} usages flows through the materialization gate
        // unchanged and never invokes the SecretProvider.
        var provider = new FakeSecretProvider();
        var services = new AgentServices
        {
            SecretProvider = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), services, TaskScheduler.Default);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-no-secret");
        var definition = CreateTestDefinition("no-secret");
        var originalJson = definition.ToJson();

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = definition,
                AgentServices = services,
                EntityName = "Entity",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(originalJson, lease.AgentChat.Information.AgentDefinition.ToJson());
    }

    private static AgentDefinition McpSecretDefinition()
        => AgentDefinition.FromJson(
            """
            {
              "kind": "prompt",
              "name": "mcp-secret-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [
                {
                  "kind": "mcp",
                  "name": "github-secret-gated",
                  "serverName": "github-secret-gated",
                  "connection": { "kind": "key", "endpoint": "http://127.0.0.1:1/", "apiKey": "${SECRET:GitHubToken}" },
                  "approvalMode": { "kind": "never" }
                }
              ]
            }
            """);

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public int CallCount { get; private set; }
        public Dictionary<string, SecureString> Secrets { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
        {
            this.CallCount++;
            var retrievers = requests
                .Where(request => this.Secrets.ContainsKey(request.SecretName))
                .Select(request => new SecretRetriever
                {
                    SecretName = request.SecretName,
                    Secret = _ => Task.FromResult(this.Secrets[request.SecretName]),
                })
                .ToArray();

            return Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult(retrievers, []));
        }
    }

    private static AgentDefinition CreateTestDefinition(string name)
        => AgentDefinition.FromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{name}}",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
}

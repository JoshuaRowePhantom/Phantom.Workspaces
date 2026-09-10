using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Immutable;
using System.Security;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
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
        private readonly Dictionary<AgentSessionId, (int RefCount, RunningAgentChat Entry, Task<AgentChat> ChatTask)> _sessions = new();

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public AgentDefinition? LastDefinition { get; private set; }
        public AgentServices? LastServices { get; private set; }

        public FakeRunningAgentChatFactory(TaskScheduler? foregroundScheduler = null)
        {
            _foregroundScheduler = foregroundScheduler ?? TaskScheduler.Default;
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
                    entryToAdd = new RunningAgentChat(sessionId, this);
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

        private async ValueTask RemoveRefAsync(AgentSessionId sessionId)
        {
            bool shouldRemove;
            RunningAgentChat? entryToRemove;

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
                    _ = DisposeChatAsync(existing.ChatTask);
                }
                else
                {
                    _sessions[sessionId] = (existing.RefCount - 1, existing.Entry, existing.ChatTask);
                    shouldRemove = false;
                    entryToRemove = null;
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
        }

        private static async Task DisposeChatAsync(Task<AgentChat> chatTask)
            => await (await chatTask.ConfigureAwait(false)).DisposeAsync();
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
            Owner = "host-A",
            OwnershipGeneration = 2,
            RuntimeEpoch = 4,
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
    public async Task TerminateAsync_MissingSession_ReturnsFalse()
    {
        var table = new RunningAgentChatTable(new FakeRunningAgentChatFactory());

        Assert.False(await table.TerminateAsync(
            new AgentSessionId("missing"),
            TestContext.Current.CancellationToken));
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

    private sealed class SnapshotChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();

        internal SnapshotChannel(string sessionId)
        {
            var epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
            var snapshot = new AgentSessionSnapshot
            {
                Information = new AgentInformation
                {
                    AgentSessionId = sessionId,
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
                        epoch,
                        1,
                        Guid.NewGuid(),
                        new SessionSnapshotEvent { Snapshot = snapshot })));
        }

        public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.incoming.Reader;

        public ValueTask DisposeAsync()
        {
            this.outgoing.Writer.TryComplete();
            this.incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

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
    }

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

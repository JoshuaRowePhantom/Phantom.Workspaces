using System.Collections.Specialized;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed partial class RemoteAgentSessionHostTests
{
    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData("idle")]
    [InlineData("terminate")]
    [InlineData("background")]
    [InlineData("failure")]
    [InlineData("replace-terminate")]
    [InlineData("replace-background")]
    public async Task CloseTab_RealRemoteTableClientOwnerRoundtrip_RequiresExactAcknowledgedLifecycle(string scenario)
    {
        await using var localFactory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(localFactory, new CloseRuntimeContextFactory());
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(), NullLoggerFactory.Instance, Mock.Of<IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        await using var owner = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = "session", ConfiguredStore = new InMemoryAgentPersistenceStore(),
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"owner","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
        });
        if (scenario != "idle")
            owner.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("working")] });
        var persistenceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retentionWrites = 0;
        await using var runtime = new RemoteAgentSessionLease("session", 1, new RuntimeEpoch { Value = Guid.NewGuid() },
            owner, false, () => Snapshot() with { Information = owner.Information, InputQueues = ((IAgentChat)owner).InputQueues.Snapshot },
            persistRetentionAsync: async (_, _) =>
            {
                retentionWrites++;
                persistenceEntered.TrySetResult();
                await releasePersistence.Task;
                if (scenario == "failure") throw new IOException("Controlled owner persistence failure");
            });
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(runtime));
        var authorization = new Mock<IAgentSessionAttachAuthorizer>();
        authorization.Setup(value => value.AuthorizeAsync(It.IsAny<TransportPeerIdentity>(),
            It.IsAny<AgentSessionAuthorizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
        await using var host = new RemoteAgentSessionHost(authorization.Object, registry, Mock.Of<IAgentSessionRuntimeHostFactory>());
        var transport = new CloseLoopbackTransport(host);
        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","remote-close","{{{entityId}}}"]],"agent-session-id":"session","host-profile-entity-id":"22222222-2222-2222-2222-222222222222","ownership-generation":1}""");
        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("session"), AgentSessionEntity = entity.Data,
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote, OwningProfileTransport = transport,
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
        };
        var lease = await table.AcquireAsync(request, TestContext.Current.CancellationToken);
        var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = entityId.ToString(), Title = "Remote", Entity = entity, AgentSessionId = "session",
        };
        tab.SetLease(lease);
        tab.SetReady(new AgentViewModel(lease.AgentChat, "remote", "", logger, TaskScheduler.FromCurrentSynchronizationContext()), logger);
        await main.OpenTabAsync(tab);
        var decision = new RemoteCloseDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var closing = main.RequestCloseTabAsync(tab);
        if (scenario == "idle")
        {
            Assert.True(await closing);
            Assert.Equal(0, decision.Calls);
            Assert.IsType<DetachIfIdleCommand>(Assert.Single(transport.Commands));
            Assert.True(runtime.HasTerminated);
            Assert.Equal(0, runtime.ViewerCount);
            Assert.Equal(0, retentionWrites);
            return;
        }
        Assert.Equal(1, decision.Calls);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        if (scenario.StartsWith("replace-", StringComparison.Ordinal))
        {
            var oldProxy = Assert.IsType<RemoteAgentChat>(lease.AgentChat);
            var terminalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oldProxy.RuntimeStateChanged += (_, _) =>
            {
                if (oldProxy.IsTerminal) terminalObserved.TrySetResult();
            };
            await runtime.TryTerminateAsync(TestContext.Current.CancellationToken);
            await terminalObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            await using var replacementOwner = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentSessionId = "session", ConfiguredStore = new InMemoryAgentPersistenceStore(),
                AgentDefinition = owner.AgentDefinition, ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            });
            await using var replacement = new RemoteAgentSessionLease("session", 1,
                new RuntimeEpoch { Value = Guid.NewGuid() }, replacementOwner, true,
                () => Snapshot() with { Information = replacementOwner.Information, InputQueues = ((IAgentChat)replacementOwner).InputQueues.Snapshot });
            await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(replacement));
            var replacementTransport = new CloseLoopbackTransport(host);
            var replacementLease = await table.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("session"), AgentSessionEntity = entity.Data,
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote, OwningProfileTransport = replacementTransport,
                ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            }, TestContext.Current.CancellationToken);
            Assert.NotSame(lease.AgentChat, replacementLease.AgentChat);
            await tab.ResetForRecreationAsync();
            tab.SetLease(replacementLease);
            var replacementLogger = new ObservableLoggerFactory();
            tab.SetReady(new AgentViewModel(replacementLease.AgentChat, "replacement", "", replacementLogger,
                TaskScheduler.FromCurrentSynchronizationContext()), replacementLogger);
            decision.Result.SetResult(scenario == "replace-terminate" ? AgentSessionCloseDecision.Terminate : AgentSessionCloseDecision.RunInBackground);
            Assert.False(await closing);
            Assert.Empty(replacementTransport.Commands);
            Assert.NotEqual(runtime.Epoch, replacement.Epoch);
            Assert.False(replacement.HasTerminated);
            Assert.Equal(1, replacement.ViewerCount);
            Assert.Same(replacementLease, tab.Lease);
            await lease.DisposeAsync();
            return;
        }
        decision.Result.SetResult(scenario == "terminate" ? AgentSessionCloseDecision.Terminate : AgentSessionCloseDecision.RunInBackground);
        if (scenario is "background" or "failure")
        {
            await persistenceEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(closing.IsCompleted);
            Assert.Equal(1, runtime.ViewerCount);
            Assert.False(runtime.ContinueInBackground);
            Assert.DoesNotContain(transport.Commands, command => command is DetachCommand);
            releasePersistence.SetResult();
        }
        Assert.Equal(scenario != "failure", await closing);
        if (scenario == "failure")
        {
            Assert.Equal(1, runtime.ViewerCount);
            Assert.False(runtime.ContinueInBackground);
            Assert.False(runtime.HasTerminated);
            Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
            Assert.IsType<SetContinueInBackgroundCommand>(Assert.Single(transport.Commands));
        }
        else if (scenario == "terminate")
        {
            Assert.True(runtime.HasTerminated);
            Assert.IsType<TerminateSessionCommand>(transport.Commands.First());
            Assert.Single(transport.Commands, command => command is TerminateSessionCommand);
            Assert.DoesNotContain(transport.Commands, command => command is SetContinueInBackgroundCommand);
        }
        else
        {
            Assert.True(runtime.ContinueInBackground);
            Assert.False(runtime.HasTerminated);
            Assert.Equal(1, retentionWrites);
            var retainedRow = Assert.Single(table.RunningSessions);
            Assert.Equal(1, retainedRow.ViewerCount);
            Assert.DoesNotContain(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
            Assert.True(retainedRow.ContinueInBackground);
            // The retained table connection owns the fallback row after its last GUI viewer
            // closes. Detaching that connection must still preserve the retained owner.
            await Assert.IsType<RemoteAgentChat>(lease.AgentChat).DetachAsync();
            await transport.Detached.Task.WaitAsync(TestContext.Current.CancellationToken);
            await transport.OwnerReleased.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, runtime.ViewerCount);
            Assert.False(runtime.HasTerminated);
            Assert.Contains(transport.Commands, command => command is SetContinueInBackgroundCommand);
            Assert.Contains(transport.Commands, command => command is DetachCommand);
            Assert.DoesNotContain(transport.Commands, command => command is TerminateSessionCommand);
        }
    }

    private sealed class RemoteCloseDecisionProvider : IAgentSessionCloseDecisionProvider
    {
        internal int Calls { get; private set; }
        internal TaskCompletionSource<AgentSessionCloseDecision> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentSessionCloseDecision> ChooseAsync(string sessionName, CancellationToken cancellationToken)
        {
            this.Calls++;
            return this.Result.Task;
        }
    }

    private sealed class CloseRuntimeContextFactory : IAgentSessionRuntimeContextFactory
    {
        public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity) => new() { Intent = Intent() };
    }

    private sealed class CloseLoopbackTransport(RemoteAgentSessionHost host) : ITransport
    {
        private readonly Channel<JsonElement> clientToOwner = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> ownerToClient = Channel.CreateUnbounded<JsonElement>();
        private RemoteAgentAttachmentLease? attachment;
        internal System.Collections.Concurrent.ConcurrentQueue<AgentSessionCommand> Commands { get; } = new();
        internal TaskCompletionSource Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task OwnerReleased => this.attachment!.Released;

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.attachment = await host.OpenAsync(new OpenAgentSessionHostRequest
            {
                Peer = Peer(), OpenRequest = AgentSessionProtocolCodec.DeserializeOpen(request),
                Channel = new CloseEndpoint(this.clientToOwner.Reader, this.ownerToClient.Writer),
            }, ct);
            return new CloseEndpoint(this.ownerToClient.Reader, new RecordingCommandWriter(this, this.clientToOwner.Writer));
        }
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            if (this.attachment is not null) await this.attachment.DisposeAsync();
            this.clientToOwner.Writer.TryComplete();
            this.ownerToClient.Writer.TryComplete();
        }

        private sealed class RecordingCommandWriter(CloseLoopbackTransport owner, ChannelWriter<JsonElement> inner) : ChannelWriter<JsonElement>
        {
            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
            public override bool TryWrite(JsonElement item)
            {
                var command = AgentSessionProtocolCodec.DeserializeCommand(item);
                owner.Commands.Enqueue(command);
                var written = inner.TryWrite(item);
                if (written && command is DetachCommand) owner.Detached.TrySetResult();
                return written;
            }
            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                => inner.WaitToWriteAsync(cancellationToken);
        }
    }

    private sealed class CloseEndpoint(ChannelReader<JsonElement> reader, ChannelWriter<JsonElement> writer) : IMessageChannel
    {
        public ChannelReader<JsonElement> Reader => reader;
        public ChannelWriter<JsonElement> Writer => writer;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

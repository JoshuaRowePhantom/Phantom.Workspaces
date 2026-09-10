using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSchema;
using GitHub.Copilot;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;
using RunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteExecutionProductionPathTests
{
    private static readonly EntityId GuiHost = new("11111111-1111-1111-1111-111111111111");
    private static readonly EntityId AgentHost = new("22222222-2222-2222-2222-222222222222");
    private static readonly EntityId ComponentHost = new("33333333-3333-3333-3333-333333333333");

    public static TheoryData<bool, bool, bool> PlacementCases => new()
    {
        { false, false, false },
        { false, false, true },
        { false, true, false },
        { false, true, true },
        { true, false, false },
        { true, false, true },
        { true, true, false },
        { true, true, true },
    };

    [Theory]
    [MemberData(nameof(PlacementCases))]
    public async Task PersistedRuntime_AllEightPlacementAndContainmentCases_LaunchOnlyOnResolvedHost(
        bool remoteAgent,
        bool remoteComponent,
        bool requiresContainment)
    {
        var gui = new HostProbe(requiresContainment);
        var agent = new HostProbe(requiresContainment);
        var component = new HostProbe(requiresContainment);
        var owner = remoteAgent ? agent : gui;
        var ownerId = remoteAgent ? AgentHost : GuiHost;
        var hostClientFactory = new RecordingClientFactory();
        var componentRuntimeFactory = new RecordingRuntimeFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(
            hostClientFactory,
            component,
            component,
            componentRuntimeFactory));
        await using var componentTransport = new LocalTransport(listeners);
        var registry = new RecordingTransportFactoryRegistry(componentTransport);
        var runtimeFactory = new AgentSessionRuntimeContextFactory(registry);
        var chatFactory = new CapturingRunningAgentChatFactory();
        var table = new RunningAgentChatTable(chatFactory, runtimeFactory);
        var entity = CreatePersistedSession(ownerId, remoteAgent, remoteComponent);

        await using var runtimeLease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("production-placement"),
                AgentSessionEntity = entity,
                AgentDefinition = CreateDefinition(),
                AgentServices = new AgentServices
                {
                    TrustProfileResolver = owner,
                    TrustProfilePolicyCompiler = owner,
                },
            },
            TestContext.Current.CancellationToken);

        var hydratedServices = Assert.IsType<AgentServices>(chatFactory.Services);
        var bindings = Assert.IsType<ExecutorBindings>(hydratedServices.ExecutorBindings);
        var trustContext = Assert.IsType<AgentExecutionTrustContext>(
            hydratedServices.AgentExecutionTrustContext);
        var options = new ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["executor"] = "model",
            },
        };
        await using var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            modelOptions: options,
            executionTrustContext: trustContext);
        var localClientFactory = new RecordingClientFactory();
        var ownerRuntimeFactory = new RecordingRuntimeFactory();
        client.SetCopilotClientFactoryForTest(localClientFactory);
        client.SetRuntimeConnectionFactoryForTest(ownerRuntimeFactory);
        client.ConfigureExecutorRouting(
            bindings,
            Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                hydratedServices.ExecutorTransportFactoryRegistry));

        var remoteClient = await client.ResolveRemoteClientForTestAsync(
            TestContext.Current.CancellationToken);
        if (remoteComponent)
        {
            Assert.IsType<CopilotClientOverTransport>(remoteClient);
            await using var session = await remoteClient!.CreateSessionAsync(
                new SessionConfig { Model = "gpt-5" },
                TestContext.Current.CancellationToken);
            Assert.Equal(1, registry.ConnectCount);
            Assert.Equal(ComponentHost.ToString(), registry.LastDescriptor!
                .Value.GetProperty(ExecutorBindings.EntityIdPropertyName).GetString());
            Assert.Equal(1, componentRuntimeFactory.CallCount);
            Assert.Equal(requiresContainment, componentRuntimeFactory.RequiresContainment);
        }
        else
        {
            Assert.Null(remoteClient);
            await client.CreateClientOptionsForTestAsync(
                workingDirectory: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, registry.ConnectCount);
            Assert.Equal(1, ownerRuntimeFactory.CallCount);
            Assert.Equal(requiresContainment, ownerRuntimeFactory.RequiresContainment);
        }

        Assert.Equal(
            remoteAgent ? AgentHost.ToString() : TrustProfile.LocalClientInstance,
            bindings.ToTopology().Resolve(Phantom.Workspaces.Llm.Core.Transport.ExecutorTarget.AgentExecutor));
        Assert.Equal(remoteComponent ? 0 : 1, owner.ResolveCount);
        Assert.Equal(remoteComponent ? 0 : 1, owner.CompileCount);
        Assert.Equal(remoteComponent ? 1 : 0, component.ResolveCount);
        Assert.Equal(remoteComponent ? 1 : 0, component.CompileCount);
        Assert.Equal(remoteAgent ? 0 : owner.ResolveCount, gui.ResolveCount);
        Assert.Equal(remoteAgent ? owner.ResolveCount : 0, agent.ResolveCount);
    }

    private static JsonElement CreatePersistedSession(
        EntityId ownerId,
        bool remoteAgent,
        bool remoteComponent)
    {
        var sessionExecutor = remoteAgent
            ? RemoteDescriptor(AgentHost)
            : ExecutorBindings.LocalDescriptor();
        var componentBindings = JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                ["model"] = remoteComponent
                    ? RemoteDescriptor(ComponentHost)
                    : ExecutorBindings.LocalDescriptor(),
            });
        return AgentSessionEntityFactory.CreateEntityData(
            new CreateAgentSessionEntityDataRequest
            {
                AgentDefinitionEntityId = new EntityId(),
                AgentDisplayName = "Production placement",
                AgentSessionId = "production-placement",
                AgentSessionNames = [new EntityName("tests", "production-placement")],
                CurrentTime = DateTimeOffset.UnixEpoch,
                ComputerName = "test-host",
                HostProfileEntityId = ownerId,
                SessionExecutor = sessionExecutor,
                ExecutorComponentBindings = componentBindings,
                TrustProfileReference = JsonSerializer.SerializeToElement("restricted"),
                ExpectedTrustProfileRevision = 17,
            });
    }

    private static JsonElement RemoteDescriptor(EntityId host) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                [ExecutorBindings.TypePropertyName] = "user-computer-profile",
                [ExecutorBindings.EntityIdPropertyName] = host.ToString(),
            });

    private static AgentDefinition CreateDefinition() =>
        AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "production-placement",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);

    private sealed class HostProbe(bool requiresContainment)
        : IRemoteTrustProfileResolver, ITrustProfileProcessPolicyCompiler
    {
        public int ResolveCount { get; private set; }
        public int CompileCount { get; private set; }

        public Task<RemoteTrustProfileResolution?> ResolveAsync(
            string profileReference,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult<RemoteTrustProfileResolution?>(
                new(new TrustProfile(), "17"));
        }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CompileCount++;
            return new TrustProfileProcessPolicyCompilation(
                requiresContainment,
                requiresContainment
                    ? CreatePolicy()
                    : null,
                []);
        }

        private static MxcProcessPolicy CreatePolicy() =>
            new(
                MxcProcessPolicy.CurrentSchemaVersion,
                [],
                [],
                [],
                new Dictionary<string, string>(),
                new MxcProcessContainment(
                    MxcContainmentBackend.ProcessContainer,
                    LeastPrivilege: true,
                    LearningMode: false,
                    PermissiveMode: false));
    }

    private sealed class RecordingRuntimeFactory : ICopilotRuntimeConnectionFactory
    {
        public int CallCount { get; private set; }
        public bool? RequiresContainment { get; private set; }

        public async Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
            AgentExecutionTrustContext trustContext,
            string? cliPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var compilation = await trustContext.GetCompilationAsync(cancellationToken);
            RequiresContainment = compilation.RequiresContainment;
            return new CopilotRuntimeConnectionLease(
                RuntimeConnection.ForStdio("copilot.exe", []),
                policyLease: null);
        }
    }

    private sealed class RecordingTransportFactoryRegistry(ITransport transport)
        : ITransportFactoryRegistry
    {
        public int ConnectCount { get; private set; }
        public JsonElement? LastDescriptor { get; private set; }

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(
            JsonElement connectionDescriptor,
            CancellationToken ct = default)
        {
            ConnectCount++;
            LastDescriptor = connectionDescriptor.Clone();
            return Task.FromResult(transport);
        }
    }

    private sealed class CapturingRunningAgentChatFactory : RunningAgentChatFactory
    {
        private readonly IAgentChat chat = Moq.Mock.Of<IAgentChat>();

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];
        public AgentServices? Services { get; private set; }

        public Task<RunningAgentChatLease> GetAsync(
            AgentSessionId sessionId,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => Task.FromResult(
                new RunningAgentChatLease(
                    sessionId,
                    this.chat,
                    () => ValueTask.CompletedTask));

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => this.GetOrCreateAsync(
                sessionId,
                definition,
                services,
                displayNameOverride,
                descriptionOverride,
                ct: ct);

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
        {
            Services = services;
            return this.GetAsync(sessionId, registerAsRunningAgent, ct);
        }
    }

    private sealed class RecordingClientFactory : ICopilotClientFactory
    {
        public ICopilotClient Create(CopilotClientOptions options) => new RecordingClient();
    }

    private sealed class RecordingClient : ICopilotClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<ICopilotSession> CreateSessionAsync(
            SessionConfig config,
            CancellationToken cancellationToken)
            => Task.FromResult<ICopilotSession>(new RecordingSession());

        public Task<ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
            => Task.FromResult<ICopilotSession>(new RecordingSession());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSession : ICopilotSession
    {
        public string SessionId => "production-session";

        public IDisposable Subscribe(Action<SessionEvent> handler) =>
            new CancellationTokenSource();

        public Task<AssistantMessageEvent?> SendAndWaitAsync(
            MessageOptions options,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
            => Task.FromResult<AssistantMessageEvent?>(null);

        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

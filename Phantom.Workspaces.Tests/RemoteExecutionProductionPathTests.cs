using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Mcp;
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
        var ownerId = remoteAgent ? AgentHost : GuiHost;
        var guiProcessExecutor = new RecordingProcessExecutor();
        var agentProcessExecutor = new RecordingProcessExecutor();
        var componentProcessExecutor = new RecordingProcessExecutor();
        var componentClientFactory = new RecordingClientFactory();
        var componentRuntimeFactory = new RecordingRuntimeFactory();
        var componentListeners = new TransportRegistry();
        componentListeners.Register(new CopilotClientTransportListener(
            componentClientFactory,
            component,
            component,
            componentRuntimeFactory));
        var remoteMcpHost = new RemoteMcpHostHandler(new AgentServices
        {
            ProcessExecutor = componentProcessExecutor,
            TrustProfileResolver = component,
            TrustProfilePolicyCompiler = component,
        });
        componentListeners.Register(new McpTransportListener(remoteMcpHost.OpenAsync));
        var registry = new RecordingTransportFactoryRegistry(
            () => new LocalTransport(componentListeners));
        var runtimeFactory = new AgentSessionRuntimeContextFactory(registry);
        var entity = CreatePersistedSession(ownerId, remoteAgent, remoteComponent);
        await using var guiFactory = new CapturingRunningAgentChatFactory();
        await using var agentFactory = new CapturingRunningAgentChatFactory();
        var guiTable = new RunningAgentChatTable(guiFactory, runtimeFactory);
        var agentTable = new RunningAgentChatTable(agentFactory, runtimeFactory);
        var guiServices = HostServices(gui, guiProcessExecutor) with
        {
            ExecutorTransportFactoryRegistry = registry,
        };
        var agentServices = HostServices(agent, agentProcessExecutor) with
        {
            ExecutorTransportFactoryRegistry = registry,
        };

        RemoteAgentSessionRuntimeRegistry? remoteRegistry = null;
        AgentSessionTransportListener? agentListener = null;
        LocalTransport? agentTransport = null;
        if (remoteAgent)
        {
            var hostFactory = new ProductionRuntimeHostFactory(
                entity,
                agentTable,
                agentServices,
                CreateDefinition());
            remoteRegistry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
            var host = new RemoteAgentSessionHost(
                new AllowAllAuthorizer(),
                remoteRegistry,
                hostFactory);
            agentListener = new AgentSessionTransportListener(host, new FixedPeerIdentityProvider());
            var agentListeners = new TransportRegistry();
            agentListeners.Register(agentListener);
            agentTransport = new LocalTransport(agentListeners);
        }

        await using var runtimeLease = await guiTable.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("production-placement"),
                AgentSessionEntity = entity,
                AgentDefinition = CreateDefinition(),
                AgentServices = guiServices,
                AcquisitionMode = remoteAgent
                    ? AgentChatAcquisitionMode.StartOrAttachRemote
                    : AgentChatAcquisitionMode.Local,
                OwningProfileTransport = agentTransport,
            },
            TestContext.Current.CancellationToken);

        var executionFactory = remoteAgent ? agentFactory : guiFactory;
        var hydratedServices = Assert.IsType<AgentServices>(executionFactory.Services);
        var ownerChat = Assert.IsType<AgentChat>(executionFactory.Chat);
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
        var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            modelOptions: options,
            executionTrustContext: trustContext);
        var ownerClientFactory = new RecordingClientFactory();
        var ownerRuntimeFactory = new RecordingRuntimeFactory();
        client.SetCopilotClientFactoryForTest(ownerClientFactory);
        client.SetRuntimeConnectionFactoryForTest(ownerRuntimeFactory);
        client.ConfigureExecutorRouting(
            bindings,
            Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                hydratedServices.ExecutorTransportFactoryRegistry));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "exercise production model routing")],
            cancellationToken: TestContext.Current.CancellationToken));
        ownerChat.RegisterOwnedResource(client);

        var mcpProvider = new McpToolContextProvider(
            CreateStdioMcpTool(),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            hydratedServices,
            bindings.ResolveComponent("mcp"),
            new ExecutorTargetRouter(
                bindings.ToTopology(),
                Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                    hydratedServices.ExecutorTransportFactoryRegistry)),
            trustContext: trustContext);
        ownerChat.RegisterOwnedResource(mcpProvider);
        var expectedProcessExecutor = remoteComponent
            ? componentProcessExecutor
            : remoteAgent ? agentProcessExecutor : guiProcessExecutor;
        var processStarted = expectedProcessExecutor.NextStartedAsync(
            TestContext.Current.CancellationToken);
        var mcpInitialization = GetToolsAsync(mcpProvider);
        var mcpStartedOrCompleted = await Task.WhenAny(
            processStarted,
            mcpInitialization);
        if (mcpStartedOrCompleted == mcpInitialization)
            await mcpInitialization;
        var processHandle = await processStarted;

        if (remoteComponent)
        {
            Assert.Equal(2, registry.ConnectCount);
            Assert.Equal(ComponentHost.ToString(), registry.LastDescriptor!
                .Value.GetProperty(ExecutorBindings.EntityIdPropertyName).GetString());
            Assert.Equal(1, componentRuntimeFactory.CallCount);
            Assert.Equal(requiresContainment, componentRuntimeFactory.RequiresContainment);
        }
        else
        {
            Assert.Equal(0, registry.ConnectCount);
            Assert.Equal(1, ownerRuntimeFactory.CallCount);
            Assert.Equal(requiresContainment, ownerRuntimeFactory.RequiresContainment);
        }

        Assert.Equal(remoteComponent ? 0 : 1, (remoteAgent ? agent : gui).ResolveCount);
        Assert.Equal(remoteComponent ? 0 : 1, (remoteAgent ? agent : gui).CompileCount);
        Assert.Equal(remoteComponent ? 2 : 0, component.ResolveCount);
        Assert.Equal(remoteComponent ? 2 : 0, component.CompileCount);
        Assert.Equal(remoteAgent ? 0 : (remoteComponent ? 0 : 1), gui.ResolveCount);
        Assert.Equal(remoteAgent ? (remoteComponent ? 0 : 1) : 0, agent.ResolveCount);
        Assert.Single(expectedProcessExecutor.Requests);
        Assert.Equal(requiresContainment, expectedProcessExecutor.Requests[0].MxcPolicy is not null);

        await runtimeLease.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await mcpInitialization);
        await processHandle.DisposedTask.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, processHandle.TerminateCount);
        Assert.Equal(1, processHandle.DisposeCount);
        Assert.Equal(1, (remoteComponent ? componentClientFactory : ownerClientFactory).DisposeCount);

        if (agentTransport is not null)
            await agentTransport.DisposeAsync();
        if (agentListener is not null)
            await agentListener.DisposeAsync();
        if (remoteRegistry is not null)
            await remoteRegistry.DisposeAsync();
    }

    [Fact]
    public async Task Takeover_ProductionHost_FencesOldTreeAndFinalReleaseDisposesReplacementTree()
    {
        var ownerTrust = new HostProbe(requiresContainment: true);
        var componentTrust = new HostProbe(requiresContainment: true);
        var componentProcesses = new RecordingProcessExecutor();
        var componentClients = new RecordingClientFactory();
        var componentListeners = new TransportRegistry();
        componentListeners.Register(new CopilotClientTransportListener(
            componentClients,
            componentTrust,
            componentTrust,
            new RecordingRuntimeFactory()));
        componentListeners.Register(new McpTransportListener(
            new RemoteMcpHostHandler(new AgentServices
            {
                ProcessExecutor = componentProcesses,
                TrustProfileResolver = componentTrust,
                TrustProfilePolicyCompiler = componentTrust,
            }).OpenAsync));
        var transportRegistry = new RecordingTransportFactoryRegistry(
            () => new LocalTransport(componentListeners));
        await using var chatFactory = new CapturingRunningAgentChatFactory();
        var chatTable = new RunningAgentChatTable(
            chatFactory,
            new AgentSessionRuntimeContextFactory(transportRegistry));
        var hostFactory = new TakeoverRuntimeHostFactory(
            chatTable,
            chatFactory,
            HostServices(ownerTrust, new RecordingProcessExecutor()) with
            {
                ExecutorTransportFactoryRegistry = transportRegistry,
            },
            CreateDefinition(),
            GuiHost,
            AgentHost);
        await using var runtimeRegistry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await using var host = new RemoteAgentSessionHost(
            new AllowAllAuthorizer(),
            runtimeRegistry,
            hostFactory);
        await using var oldChannel = new TestMessageChannel();
        await using var oldAttachment = await host.OpenAsync(
            OpenHostRequest(
                oldChannel,
                AgentSessionOpenIntent.Start,
                GuiHost,
                ownershipGeneration: 0,
                "old-viewer"),
            TestContext.Current.CancellationToken);

        var oldRuntime = Assert.Single(hostFactory.Runtimes);
        var oldServices = Assert.Single(hostFactory.Services);
        var oldContext = Assert.IsType<AgentExecutionTrustContext>(
            oldServices.AgentExecutionTrustContext);
        Assert.Equal("restricted", oldContext.RemoteReference!.Id);
        Assert.Equal("17", oldContext.RemoteReference.ExpectedRevision);
        await oldContext.GetCompilationAsync(TestContext.Current.CancellationToken);
        var oldChildResource = await AddOwnedChildAsync(oldRuntime.Chat);
        var oldResources = await ExerciseRemoteComponentsAsync(
            oldRuntime.Chat,
            oldServices,
            componentProcesses);

        await host.TakeOverAsync(
            Peer(),
            new AgentSessionTakeoverRequest
            {
                AgentSessionId = "production-takeover",
                ExpectedOwningProfileEntityId = GuiHost.ToString(),
                ExpectedOwnershipGeneration = 0,
                NewOwningProfileEntityId = AgentHost.ToString(),
                CorrelationId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken);

        Assert.True(oldRuntime.IsFenced);
        Assert.True(oldChildResource.Disposed);
        Assert.Equal(1, oldResources.ProcessHandle.TerminateCount);
        Assert.Equal(1, oldResources.ProcessHandle.DisposeCount);
        Assert.Equal(1, componentClients.DisposeCount);

        var newRuntime = Assert.Single(hostFactory.Runtimes.Skip(1));
        var newServices = Assert.Single(hostFactory.Services.Skip(1));
        var newContext = Assert.IsType<AgentExecutionTrustContext>(
            newServices.AgentExecutionTrustContext);
        Assert.NotSame(oldContext, newContext);
        Assert.Equal("restricted", newContext.RemoteReference!.Id);
        Assert.Equal("17", newContext.RemoteReference.ExpectedRevision);
        await newContext.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, ownerTrust.ResolveCount);
        Assert.Equal(2, ownerTrust.CompileCount);

        var newChildResource = await AddOwnedChildAsync(newRuntime.Chat);
        var newResources = await ExerciseRemoteComponentsAsync(
            newRuntime.Chat,
            newServices,
            componentProcesses);
        await using var newChannel = new TestMessageChannel();
        var finalAttachment = await host.OpenAsync(
            OpenHostRequest(
                newChannel,
                AgentSessionOpenIntent.Attach,
                AgentHost,
                ownershipGeneration: 1,
                "final-viewer"),
            TestContext.Current.CancellationToken);

        await finalAttachment.DisposeAsync();

        Assert.True(newRuntime.IsFenced);
        Assert.True(newChildResource.Disposed);
        Assert.Equal(1, newResources.ProcessHandle.TerminateCount);
        Assert.Equal(1, newResources.ProcessHandle.DisposeCount);
        Assert.Equal(2, componentClients.DisposeCount);
        Assert.Equal(4, componentTrust.ResolveCount);
        Assert.Equal(4, componentTrust.CompileCount);
        Assert.Null(await runtimeRegistry.TryGetAsync(
            "production-takeover",
            1,
            TestContext.Current.CancellationToken));
    }

    private static OpenAgentSessionHostRequest OpenHostRequest(
        IMessageChannel channel,
        AgentSessionOpenIntent intent,
        EntityId owner,
        long ownershipGeneration,
        string attachmentToken) => new()
    {
        Peer = Peer(),
        OpenRequest = new AgentSessionOpenRequest
        {
            ProtocolVersion = 1,
            AgentSessionId = "production-takeover",
            ExpectedOwningProfileEntityId = owner.ToString(),
            ExpectedOwnershipGeneration = ownershipGeneration,
            OpenIntent = intent,
            AttachmentToken = attachmentToken,
            Capabilities = [],
        },
        Channel = channel,
    };

    private static TransportPeerIdentity Peer() => new()
    {
        AuthenticationScheme = "test",
        StablePeerId = "production-client",
        UserEntityId = GuiHost.ToString(),
    };

    private static async Task<TrackingResource> AddOwnedChildAsync(IAgentChat chat)
    {
        var ownerChat = Assert.IsType<AgentChat>(chat);
    _ = await ownerChat.GetOrCreateAsync(
            $"child-{Guid.NewGuid():n}",
            CreateDefinition(),
        "production-lifecycle");
    var child = Assert.IsType<AgentChat>(ownerChat.SubAgents[^1]);
    var resource = new TrackingResource();
    child.RegisterOwnedResource(resource);
        return resource;
    }

    private static async Task<RoutedRuntimeResources> ExerciseRemoteComponentsAsync(
        IAgentChat chat,
        AgentServices services,
        RecordingProcessExecutor processExecutor)
    {
        var ownerChat = Assert.IsType<AgentChat>(chat);
        var bindings = Assert.IsType<ExecutorBindings>(services.ExecutorBindings);
        var trustContext = Assert.IsType<AgentExecutionTrustContext>(
            services.AgentExecutionTrustContext);
        var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            modelOptions: new ModelOptions
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["executor"] = "model",
                },
            },
            executionTrustContext: trustContext);
        client.SetCopilotClientFactoryForTest(new RecordingClientFactory());
        client.SetRuntimeConnectionFactoryForTest(new RecordingRuntimeFactory());
        client.ConfigureExecutorRouting(
            bindings,
            Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                services.ExecutorTransportFactoryRegistry));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "exercise takeover routing")],
            cancellationToken: TestContext.Current.CancellationToken));
        ownerChat.RegisterOwnedResource(client);

        var provider = new McpToolContextProvider(
            CreateStdioMcpTool(),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services,
            bindings.ResolveComponent("mcp"),
            new ExecutorTargetRouter(
                bindings.ToTopology(),
                Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                    services.ExecutorTransportFactoryRegistry)),
            trustContext: trustContext);
        ownerChat.RegisterOwnedResource(provider);
        var processStarted = processExecutor.NextStartedAsync(TestContext.Current.CancellationToken);
        var initialization = GetToolsAsync(provider);
        var startedOrCompleted = await Task.WhenAny(processStarted, initialization);
        if (startedOrCompleted == initialization)
            await initialization;
        return new RoutedRuntimeResources(
            initialization,
            await processStarted);
    }

    private static JsonElement CreatePersistedSession(
        EntityId ownerId,
        bool remoteAgent,
        bool remoteComponent,
        string sessionId = "production-placement",
        long ownershipGeneration = 0)
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
                ["mcp"] = remoteComponent
                    ? RemoteDescriptor(ComponentHost)
                    : ExecutorBindings.LocalDescriptor(),
            });
        return AgentSessionEntityFactory.CreateEntityData(
            new CreateAgentSessionEntityDataRequest
            {
                AgentDefinitionEntityId = new EntityId(),
                AgentDisplayName = "Production placement",
                AgentSessionId = sessionId,
                AgentSessionNames = [new EntityName("tests", sessionId)],
                CurrentTime = DateTimeOffset.UnixEpoch,
                ComputerName = "test-host",
                HostProfileEntityId = ownerId,
                SessionExecutor = sessionExecutor,
                ExecutorComponentBindings = componentBindings,
                OwnershipGeneration = ownershipGeneration,
                TrustProfileReference = JsonSerializer.SerializeToElement("restricted"),
                ExpectedTrustProfileRevision = "17",
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

    private static AgentServices HostServices(
        HostProbe trust,
        RecordingProcessExecutor processExecutor) => new()
    {
        TrustProfileResolver = trust,
        TrustProfilePolicyCompiler = trust,
        ProcessExecutor = processExecutor,
    };

    private static PhantomMcpTool CreateStdioMcpTool() => new()
    {
        Name = "placement-mcp",
        Connection = new AnonymousConnection
        {
            Endpoint = "stdio://?command=dotnet",
        },
    };

    private static async Task<AITool[]> GetToolsAsync(McpToolContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(
            provider,
            agent,
            session,
            TestContext.Current.CancellationToken);
    }

    private static AgentSessionSnapshot Snapshot(IAgentChat chat) => new()
    {
        Information = chat.Information,
        Usage = chat.Usage,
        InputQueues = chat.InputQueues.Snapshot,
        IsBusy = chat.IsBusy,
        History = chat.History.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        RunningItems = chat.RunningItems.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        Tools = chat.GetToolSnapshot().Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        Subagents = chat.SubAgents.Select(item => JsonSerializer.SerializeToElement(item, item.GetType())).ToArray(),
        Modals = chat.Modals.ToArray(),
        ContinueInBackground = false,
        ViewerCount = 0,
    };

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

    private sealed class RecordingTransportFactoryRegistry(Func<ITransport> createTransport)
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
            return Task.FromResult(createTransport());
        }
    }

    private sealed class CapturingRunningAgentChatFactory : RunningAgentChatFactory, IAsyncDisposable
    {
        private readonly AgentChatFactory inner = new(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            TaskScheduler.Default);

        public ObservableCollection<RunningAgentChat> RunningSessions => this.inner.RunningSessions;
        public AgentServices? Services { get; private set; }
        public IAgentChat? Chat { get; private set; }

        public Task<RunningAgentChatLease> GetAsync(
            AgentSessionId sessionId,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => this.inner.GetAsync(sessionId, registerAsRunningAgent, ct);

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => this.CaptureAsync(this.inner.CreateAsync(
                definition,
                sessionId,
                services,
                displayNameOverride,
                descriptionOverride,
                nameOverride,
                ct));

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => this.CaptureAsync(this.inner.GetOrCreateAsync(
                sessionId,
                definition,
                services,
                displayNameOverride,
                descriptionOverride,
                registerAsRunningAgent,
                ct), services);

        private async Task<RunningAgentChatLease> CaptureAsync(
            Task<RunningAgentChatLease> acquisition,
            AgentServices? services = null)
        {
            Services = services;
            var lease = await acquisition;
            Chat = lease.AgentChat;
            return lease;
        }

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }

    private sealed class RecordingClientFactory : ICopilotClientFactory
    {
        public int DisposeCount { get; private set; }

        public ICopilotClient Create(CopilotClientOptions options) =>
            new RecordingClient(() => DisposeCount++);
    }

    private sealed class RecordingClient(Action onDispose) : ICopilotClient
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

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
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

    private sealed class RecordingProcessExecutor : IProcessExecutor
    {
        private readonly Channel<RecordingProcessHandle> started =
            Channel.CreateUnbounded<RecordingProcessHandle>();

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Requests.Add(request);
            var handle = new RecordingProcessHandle();
            this.started.Writer.TryWrite(handle);
            return handle;
        }

        public async Task<RecordingProcessHandle> NextStartedAsync(CancellationToken cancellationToken)
            => await this.started.Reader.ReadAsync(cancellationToken);
    }

    private sealed class RecordingProcessHandle : IProcessHandle
    {
        private readonly BlockingReadStream output = new();
        private readonly BlockingReadStream error = new();
        private readonly TaskCompletionSource<ProcessExitResult> exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int terminated;
        private int disposed;

        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput => this.output;
        public Stream StandardError => this.error;
        public ProcessLaunchInfo LaunchInfo { get; } = new()
        {
            ProcessId = 42,
            IsContained = false,
            Warnings = [],
            PathCategory = ProcessPathCategory.CallerProvided,
            LaunchMechanism = ProcessLaunchMechanism.OrdinaryProcess,
            CreationStatusAvailable = true,
            CreateProcessSucceeded = true,
            CreateProcessWin32Error = null,
            SdkSpawnSucceeded = null,
            JobConfigured = null,
            JobAssigned = null,
            ResumeSucceeded = null,
        };
        public int TerminateCount => Volatile.Read(ref this.terminated);
        public int DisposeCount => Volatile.Read(ref this.disposed);
        public Task DisposedTask => this.disposeCompleted.Task;

        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default)
            => this.exited.Task.WaitAsync(cancellationToken);

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Kill();
            return Task.CompletedTask;
        }

        public void Kill()
        {
            if (Interlocked.Exchange(ref this.terminated, 1) != 0)
                return;
            this.output.Dispose();
            this.error.Dispose();
            this.exited.TrySetResult(ProcessExitResult.Create(0, false, null));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                Kill();
                this.StandardInput.Dispose();
                this.disposeCompleted.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await this.completed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.completed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class AllowAllAuthorizer : IAgentSessionAttachAuthorizer
    {
        public ValueTask<AgentSessionAuthorizationDecision> AuthorizeAsync(
            TransportPeerIdentity peer,
            AgentSessionAuthorizationRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentSessionAuthorizationDecision { IsAllowed = true });
        }
    }

    private sealed class FixedPeerIdentityProvider : ITransportPeerIdentityProvider
    {
        public TransportPeerIdentity GetRequiredIdentity(IMessageChannel channel) => new()
        {
            AuthenticationScheme = "test",
            StablePeerId = "matrix-client",
            UserEntityId = GuiHost.ToString(),
        };
    }

    private sealed record RoutedRuntimeResources(
        Task<AITool[]> Initialization,
        RecordingProcessHandle ProcessHandle);

    private sealed class TrackingResource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> input = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> output = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.output.Writer;
        public ChannelReader<JsonElement> Reader => this.input.Reader;

        public ValueTask DisposeAsync()
        {
            this.input.Writer.TryComplete();
            this.output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TakeoverRuntimeHostFactory(
        RunningAgentChatTable ownerTable,
        CapturingRunningAgentChatFactory chatFactory,
        AgentServices ownerServices,
        AgentDefinition definition,
        EntityId initialOwner,
        EntityId takeoverOwner) : IAgentSessionRuntimeHostFactory
    {
        private readonly AgentSessionRuntimeContextFactory contextFactory =
            new(Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                ownerServices.ExecutorTransportFactoryRegistry));
        private EntityId currentOwner = initialOwner;
        private long currentGeneration;

        public List<RemoteAgentSessionLease> Runtimes { get; } = [];
        public List<AgentServices> Services { get; } = [];

        public ValueTask<PersistedAgentSessionRuntimeIntent?> LoadIntentAsync(
            string sessionId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var entity = CreatePersistedSession(
                this.currentOwner,
                remoteAgent: false,
                remoteComponent: true,
                sessionId,
                this.currentGeneration);
            return ValueTask.FromResult<PersistedAgentSessionRuntimeIntent?>(
                this.contextFactory.Create(entity).Intent);
        }

        public async Task<RemoteAgentSessionLease> StartAsync(
            PersistedAgentSessionRuntimeIntent intent,
            CancellationToken ct)
        {
            var entity = CreatePersistedSession(
                this.currentOwner,
                remoteAgent: false,
                remoteComponent: true,
                intent.AgentSessionId,
                intent.OwnershipGeneration);
            var lease = await ownerTable.AcquireAsync(
                new AcquireAgentChatRequest
                {
                    AgentSessionId = new AgentSessionId(intent.AgentSessionId),
                    AgentSessionEntity = entity,
                    AgentDefinition = definition,
                    AgentServices = ownerServices,
                },
                ct);
            var services = Assert.IsType<AgentServices>(chatFactory.Services);
            var runtime = new RemoteAgentSessionLease(
                intent.AgentSessionId,
                intent.OwnershipGeneration,
                new RuntimeEpoch { Value = Guid.NewGuid() },
                lease.AgentChat,
                continueInBackground: false,
                () => Snapshot(lease.AgentChat),
                runtimeLifetime: lease);
            this.Services.Add(services);
            this.Runtimes.Add(runtime);
            return runtime;
        }

        public ValueTask<bool> TryTakeOverAsync(
            AgentSessionTakeoverRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.ExpectedOwnershipGeneration != this.currentGeneration
                || !string.Equals(
                    request.ExpectedOwningProfileEntityId,
                    this.currentOwner.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                || takeoverOwner != new EntityId(request.NewOwningProfileEntityId))
            {
                return ValueTask.FromResult(false);
            }

            this.currentOwner = takeoverOwner;
            this.currentGeneration++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class ProductionRuntimeHostFactory(
        JsonElement entity,
        RunningAgentChatTable ownerTable,
        AgentServices ownerServices,
        AgentDefinition definition) : IAgentSessionRuntimeHostFactory
    {
        private readonly AgentSessionRuntimeContextFactory contextFactory =
            new(Assert.IsAssignableFrom<ITransportFactoryRegistry>(
                ownerServices.ExecutorTransportFactoryRegistry));

        public ValueTask<PersistedAgentSessionRuntimeIntent?> LoadIntentAsync(
            string sessionId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PersistedAgentSessionRuntimeIntent?>(
                this.contextFactory.Create(entity).Intent);
        }

        public async Task<RemoteAgentSessionLease> StartAsync(
            PersistedAgentSessionRuntimeIntent intent,
            CancellationToken ct)
        {
            var lease = await ownerTable.AcquireAsync(
                new AcquireAgentChatRequest
                {
                    AgentSessionId = new AgentSessionId(intent.AgentSessionId),
                    AgentSessionEntity = entity,
                    AgentDefinition = definition,
                    AgentServices = ownerServices,
                },
                ct);
            return new RemoteAgentSessionLease(
                intent.AgentSessionId,
                intent.OwnershipGeneration,
                new RuntimeEpoch { Value = Guid.NewGuid() },
                lease.AgentChat,
                continueInBackground: false,
                () => Snapshot(lease.AgentChat),
                runtimeLifetime: lease);
        }

        public ValueTask<bool> TryTakeOverAsync(
            AgentSessionTakeoverRequest request,
            CancellationToken ct)
            => ValueTask.FromResult(false);
    }
}

using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesTransportCompositionTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Composition_RuntimeHostListener_EmitsRepresentativeEventToProcessFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "composition-logging-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            var data = await CreateSeededDataAccessLayerAsync();
            var session = new WorkspaceEntitySession
            {
                UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                UserComputerProfileEntityId = LocalProfileId,
            };
            await using var composition = new WorkspacesTransportComposition(
                data, session, process, runningAgentChats: Mock.Of<IRunningAgentChatTable>());
            await using var channel = new StubMessageChannel();
            using var request = JsonDocument.Parse(
                """{"type":"attach-agent-session","prompt":"private-sentinel"}""");

            await composition.LocalListeners.OnChannelOpenAsync(request.RootElement, channel, Ct());

            var contents = ProcessLogTestFile.ReadAll(directory);
            Assert.Equal(1, contents.Split("Remote agent-session attach failed", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("private-sentinel", contents, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsLocalTransportFactory()
    {
        await using var composition = await CreateCompositionAsync();

        using var descriptor = JsonDocument.Parse("""{"type":"local"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsUserComputerProfileTransportFactory()
    {
        await using var composition = await CreateCompositionAsync();

        // The session's own profile id resolves to a local descriptor, so the user-computer-profile
        // factory routes back through the registry to the LocalTransportFactory: proves both factories
        // are registered and built in the composition.
        using var descriptor = JsonDocument.Parse(
            """{"type":"user-computer-profile","entity-id":"11111111-1111-1111-1111-111111111111"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_UnknownDescriptor_ThrowsTransportException()
    {
        await using var composition = await CreateCompositionAsync();

        using var descriptor = JsonDocument.Parse("""{"type":"nonexistent-transport"}""");
        await Assert.ThrowsAsync<TransportException>(
            () => composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct()));
    }

    [Fact]
    public async Task Composition_ExposesTrustedExecutorAndHostSurfaces()
    {
        await using var composition = await CreateCompositionAsync();

        Assert.True(composition.TrustedExecutor.CanExecute("some-target"));
        Assert.NotNull(composition.TransportHost);
        Assert.NotNull(composition.ConnectionStatusRegistry);
        Assert.NotNull(composition.LocalListeners);
        Assert.Empty(composition.HubFactories);
    }

    [Fact]
    public async Task Composition_PublishesTransportFactoryRegistry()
    {
        var provider = new TransportFactoryRegistryProvider();
        await using var composition = await CreateCompositionAsync(agentServices: null, provider);

        Assert.Same(composition.TransportFactoryRegistry, provider.Registry);
    }

    [Fact]
    public async Task Composition_LocalListeners_ServesChatClientChannelInProduction()
    {
        // Issue #1314: the production WorkspacesTransportComposition must register a
        // ChatClientTransportListener on LocalListeners so that an incoming `chat-client`
        // channel carrying an `agent-definition` is dispatched to a listener that builds
        // the executor IChatClient via AgentFactory. Without this, LocalListeners is empty
        // and remote chat-client channels have no listener in production.
        await using var composition = await CreateCompositionAsync();

        var agentDef = new AgentSchema.PromptAgent
        {
            Name = "echo-agent",
            Instructions = "",
            Model = new AgentSchema.Model { Provider = "echo", Id = "echo-model" },
        };
        var agentDefJson = agentDef.ToJson();
        var openRequest = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = agentDefJson,
        }).RootElement.Clone();

        var channel = new StubMessageChannel();
        var handle = await composition.LocalListeners.OnChannelOpenAsync(openRequest, channel, Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task Composition_ExposesRemoteMcpHostHandler()
    {
        await using var composition = await CreateCompositionAsync();

        Assert.NotNull(composition.RemoteMcpHostHandler);
    }

    [Fact]
    public async Task Composition_RegistersProductionMcpTransportListener()
    {
        // Issue #1438: the production composition must register an McpTransportListener on
        // LocalListeners so an incoming `{"type":"mcp","connection":{...}}` channel — opened by a
        // remote-bound McpToolContextProvider on another machine — is served by this machine's
        // RemoteMcpHostHandler. A connection with no endpoint is not hostable, but the listener still
        // accepts the `mcp` channel and returns a session handle (with a null inner host).
        await using var composition = await CreateCompositionAsync();

        var openRequest = Json("""{"type":"mcp","connection":{}}""");
        var channel = new StubMessageChannel();
        var handle = await composition.LocalListeners.OnChannelOpenAsync(openRequest, channel, Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task Composition_WithRunningChats_AuthenticatesAndDispatchesAgentSessionLocally()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync();
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        await using var composition = new WorkspacesTransportComposition(
            dataAccessLayer,
            session,
            NullLoggerFactory.Instance,
            runningAgentChats: Mock.Of<IRunningAgentChatTable>());
        using var localDescriptor = JsonDocument.Parse("""{"type":"local"}""");
        await using var transport = await composition.TransportFactoryRegistry.ConnectToAsync(
            localDescriptor.RootElement, Ct());
        var request = AgentSessionProtocolCodec.SerializeOpen(new AgentSessionOpenRequest
        {
            ProtocolVersion = 1,
            AgentSessionId = "missing",
            ExpectedOwningProfileEntityId = LocalProfileId.ToString(),
            ExpectedOwnershipGeneration = 1,
            OpenIntent = AgentSessionOpenIntent.Attach,
            AttachmentToken = Guid.NewGuid().ToString("N"),
            Capabilities = [],
        });

        await using var channel = await transport.ConnectToMessageChannelAsync(request, Ct());
        var response = await channel.Reader.ReadAsync(Ct());

        Assert.Contains("not-found", response.GetRawText(), StringComparison.Ordinal);
        Assert.NotNull(composition.AgentSessionPeerIdentities);
    }

    [Fact]
    public async Task ClientOnlyHost_BuildsLocalCopilotClient()
    {
        // Issue #1443: the production composition must register a client-only
        // CopilotClientTransportListener on LocalListeners so an incoming
        // `{"type":"copilot-sdk-session"}` channel — opened by a model-bound CopilotSdkChatClient on
        // another machine — is served by building a LOCAL ICopilotClient here (via the injected
        // AgentServices.CopilotClientFactory) and bridging only its SDK session over the channel.
        var factory = new StubCopilotClientFactory();
        var agentServices = new Phantom.Workspaces.Llm.AgentServices { CopilotClientFactory = factory };
        await using var composition = await CreateCompositionAsync(agentServices);

        var openRequest =
            Phantom.Workspaces.Llm.Core.Transport.Chat.CopilotSessionTransportFrames
                .BuildConnectionRequest();
        var channel = new StubMessageChannel();
        var handle = await composition.LocalListeners.OnChannelOpenAsync(openRequest, channel, Ct());

        Assert.NotNull(handle);
        Assert.True(factory.CreateCalled);
        await handle!.DisposeAsync();
    }

    private sealed class StubCopilotClientFactory : Phantom.Workspaces.Llm.Copilot.ICopilotClientFactory
    {
        public bool CreateCalled { get; private set; }

        public Phantom.Workspaces.Llm.Copilot.ICopilotClient Create(GitHub.Copilot.CopilotClientOptions options)
        {
            this.CreateCalled = true;
            return new StubCopilotClient();
        }

        private sealed class StubCopilotClient : Phantom.Workspaces.Llm.Copilot.ICopilotClient
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<System.Collections.Generic.IReadOnlyList<GitHub.Copilot.ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<System.Collections.Generic.IReadOnlyList<GitHub.Copilot.ModelInfo>>(System.Array.Empty<GitHub.Copilot.ModelInfo>());

            public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> CreateSessionAsync(GitHub.Copilot.SessionConfig config, CancellationToken cancellationToken)
                => throw new System.NotSupportedException();

            public Task<Phantom.Workspaces.Llm.Copilot.ICopilotSession> ResumeSessionAsync(string sessionId, GitHub.Copilot.ResumeSessionConfig config, CancellationToken cancellationToken)
                => throw new System.NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubMessageChannel : IMessageChannel
    {
        private readonly System.Threading.Channels.Channel<JsonElement> reader
            = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly System.Threading.Channels.Channel<JsonElement> writer
            = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

        public System.Threading.Channels.ChannelReader<JsonElement> Reader => this.reader.Reader;

        public System.Threading.Channels.ChannelWriter<JsonElement> Writer => this.writer.Writer;

        public ValueTask DisposeAsync()
        {
            this.reader.Writer.TryComplete();
            this.writer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Composition_WithHubFactories_ExposesThemToTransportHost()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync();
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        var hubFactory = new Phantom.Workspaces.Transport.ReverseHttp.ReverseHttpClientTransportFactory(
            "http://localhost:5282",
            LocalProfileId.ToString());

        await using var composition = new WorkspacesTransportComposition(dataAccessLayer, session, NullLoggerFactory.Instance, [hubFactory]);

        var exposed = Assert.Single(composition.HubFactories);
        Assert.Same(hubFactory, exposed);
        Assert.Same(hubFactory, Assert.Single(composition.TransportHost.HubFactories));
    }

    [Fact]
    public async Task WorkspacesTransportComposition_InboundRegistrySharedWithWebHost_RouterObservesInboundRegistrations()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(includeRemoteProfile: true);
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        await using var composition = new WorkspacesTransportComposition(dataAccessLayer, session, NullLoggerFactory.Instance);
        var registrationChannel = new StubMessageChannel();
        using var registrationRequest = JsonDocument.Parse(
            """{"type":"reverse-register","entity-id":"22222222-2222-4222-8222-222222222222"}""");
        await using var registration = await composition.ReverseHttpServerTransportFactory.OnChannelOpenAsync(
            registrationRequest.RootElement,
            registrationChannel,
            Ct());
        using var target = JsonDocument.Parse(
            """{"type":"user-computer-profile","entity-id":"22222222-2222-4222-8222-222222222222"}""");

        await using var transport = await composition.TransportFactoryRegistry.ConnectToAsync(target.RootElement, Ct());

        Assert.NotNull(transport);
        Assert.True(composition.ReverseHttpServerTransportFactory.IsRegistered(
            "22222222-2222-4222-8222-222222222222"));
    }

    [Fact]
    public async Task MainWindowReverseHttpRegistration_NonWebSourceDaemon_WiresSharedRegistryAndRouteStore()
    {
        await using var composition = await CreateCompositionAsync();

        Assert.NotNull(composition.ReachabilityRouteStore);
        Assert.NotNull(composition.ReverseHttpServerTransportFactory);
        Assert.Empty(composition.HubFactories);
    }

    private static Task<WorkspacesTransportComposition> CreateCompositionAsync()
        => CreateCompositionAsync(agentServices: null);

    private static async Task<WorkspacesTransportComposition> CreateCompositionAsync(
        Phantom.Workspaces.Llm.AgentServices? agentServices,
        TransportFactoryRegistryProvider? registryProvider = null)
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync();
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        return new WorkspacesTransportComposition(
            dataAccessLayer,
            session,
            NullLoggerFactory.Instance,
            hubFactories: null,
            agentServices: agentServices,
            registryProvider: registryProvider);
    }

    private static async Task<IDataAccessLayer> CreateSeededDataAccessLayerAsync(bool includeRemoteProfile = false)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var entities = new List<JsonElement>
        {
                Json(
                    """
                    {
                      "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                      "entity-types": ["entity", "user"],
                      "names": [["users", "username", "composition-test"]]
                    }
                    """),
                Json(
                    """
                    {
                      "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "name", "composition-test"]]
                    }
                    """),
                Json(
                    """
                    {
                      "entity-id": "11111111-1111-1111-1111-111111111111",
                      "entity-types": ["entity", "user-computer-profile"],
                      "computer-reference": ["computers", "name", "composition-test"],
                      "user-reference": ["users", "username", "composition-test"]
                    }
                    """),
        };
        if (includeRemoteProfile)
        {
            entities.Add(
                Json(
                    """
                    {
                      "entity-id": "22222222-2222-4222-8222-222222222223",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "name", "composition-remote"]]
                    }
                    """));
            entities.Add(
                Json(
                    """
                    {
                      "entity-id": "22222222-2222-4222-8222-222222222222",
                      "entity-types": ["entity", "user-computer-profile"],
                      "computer-reference": ["computers", "name", "composition-remote"],
                      "user-reference": ["users", "username", "composition-test"]
                    }
                    """));
        }

        await fixture.SeedManyValidAsync(entities);
        return fixture.DataAccessLayer;
    }

    private static CancellationToken Ct() => new CancellationTokenSource(System.TimeSpan.FromSeconds(10)).Token;

}

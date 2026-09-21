using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.Shell;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Issue #1083 — full agent-stack scenarios across two logical Phantom.Workspaces instances,
/// exercised over the in-process forward-HTTP and reverse-HTTP harnesses, fast/offline.
///
/// <para><b>Two-instance identity is real, not decorative.</b> Both instances are represented
/// end-to-end by two real <c>user-computer-profile</c> entities seeded via
/// <see cref="ValidatingEntitySeedFixture"/> through the production validation pipeline.
/// Instance A opens a <c>{"type":"user-computer-profile","entity-id":&lt;B&gt;}</c> descriptor which
/// the production <see cref="UserComputerProfileTransportFactory"/> resolves through A's real
/// <see cref="TransportFactoryRegistry"/>. These compatibility tests still exercise a trusted
/// transient route; durable route discovery is covered by
/// <see cref="AgentStackMultiInstanceReachabilityTests"/>.</para>
///
/// <para><b>Forward-HTTP cells</b> plumb a real <see cref="HttpTransport"/> against a real
/// <see cref="ServerHttpTransport"/> in-process (via <see cref="PairedWebSocket"/>) and register
/// the pair with the real <see cref="InProcessHttpServerTransportFactory"/> for lease-tracking
/// parity with <see cref="LeaseExpiryTests"/>. Instance A's factory registry then holds the
/// forward harness itself (as an <see cref="ITransportFactory"/>) so that when the profile router
/// resolves B's <c>connection-descriptor</c>, the transport handed back is the production
/// <see cref="HttpTransport"/> — no <c>LocalTransport</c> leaf substitute.</para>
///
/// <para><b>Reverse-HTTP cells</b> reuse <see cref="HubRelayHarness"/>, driving the real
/// <c>ReverseHttpForwardingTransportFactory</c> + <c>ReverseExecutionDispatcher</c>. Instance A's
/// registry holds the harness's forwarding factory so B's
/// <c>{"type":"reverse-http","hub-urls":[...],"entity-id":...}</c> connection-descriptor is
/// dispatched through the real hub-relay path.</para>
///
/// <para>In both configurations the executor's <see cref="TransportRegistry"/> hosts a
/// <see cref="ChatClientTransportListener"/> around the real
/// <see cref="EchoChatClient"/> for chat, a <see cref="ShellTransportListener"/> for shell, and a
/// <see cref="PersistingEchoChatClient"/> decorator that writes turn pairs to an
/// <see cref="IAgentPersistenceStore"/> on Instance B for the persistence cells.</para>
/// </summary>
public sealed class AgentStackCrossInstanceTests
{
    private static readonly EntityId InstanceAProfileId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa1083");
    private static readonly EntityId InstanceBProfileId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb1083");
    private static readonly EntityId InstanceAComputerId = new("cccccccc-cccc-cccc-cccc-cccccccc1083");
    private static readonly EntityId InstanceBComputerId = new("dddddddd-dddd-dddd-dddd-dddddddd1083");
    private static readonly EntityId SharedUserId = new("eeeeeeee-eeee-eeee-eeee-eeeeeeee1083");

    // -------------------- forward HTTP --------------------

    [Fact]
    public async Task ForwardHttp_ChatTurn_EchoRoundTripsFromInstanceAToInstanceB()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(new EchoChatClient()));

        await using var forward = await ForwardHttpInstance.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildForwardProfileRoutingAsync(forward, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "hello from A", ct);

        Assert.Equal("hello from A", text);
    }

    [Fact]
    public async Task ForwardHttp_ShellInvocation_StreamsOutputFromInstanceBBackToInstanceA()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ShellTransportListener());

        await using var forward = await ForwardHttpInstance.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildForwardProfileRoutingAsync(forward, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        await using var shell = new ShellOverTransport(transport, ShellEchoRequest("cross-instance-hello"));
        await shell.OpenAsync(ct);

        var stdout = await ReadUntilFirstMatchAsync(shell.Stream, "cross-instance-hello", ct);

        Assert.Contains("cross-instance-hello", stdout);
    }

    [Fact]
    public async Task ForwardHttp_AgentSession_PersistsMessagesInInstanceBStore()
    {
        var ct = TransportScenarioSupport.TestToken();
        var store = AgentPersistenceStoreFactory.CreateInMemory();
        const string sessionId = "forward-cross-instance-session";

        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(new PersistingEchoChatClient(store, sessionId)));

        await using var forward = await ForwardHttpInstance.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildForwardProfileRoutingAsync(forward, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "persist please", ct);

        Assert.Equal("persist please", text);

        var persisted = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = sessionId }, ct);

        Assert.Equal(2, persisted.Length);
        Assert.Equal(ChatRole.User, persisted[0].Role);
        Assert.Equal("persist please", persisted[0].Text);
        Assert.Equal(ChatRole.Assistant, persisted[1].Role);
        Assert.Equal("persist please", persisted[1].Text);
    }

    // -------------------- forward-HTTP guard tests (#1127) --------------------

    [Fact]
    public async Task ForwardHttp_LeafWire_IsRealHttpTransport()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(new EchoChatClient()));

        await using var forward = await ForwardHttpInstance.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildForwardProfileRoutingAsync(forward, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);

        var unwrapped = transport is ForwardHttpInstance.NonOwningTransport nonOwning ? nonOwning.Inner : transport;
        Assert.IsType<HttpTransport>(unwrapped);
    }

    [Fact]
    public void ForwardHttp_HarnessSetup_DoesNotReferenceLocalTransport()
    {
        var testType = typeof(AgentStackCrossInstanceTests);
        var relatedTypes = testType.Assembly.GetTypes()
            .Where(t => t == testType || t.DeclaringType == testType)
            .ToArray();

        Assert.DoesNotContain(relatedTypes, t => t.Name == "PreConnectedForwardTransportFactory");

        var localTransportName = typeof(Phantom.Workspaces.Transport.Local.LocalTransport).FullName!;
        foreach (var type in relatedTypes)
        {
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                Assert.NotEqual(localTransportName, field.FieldType.FullName);
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                Assert.NotEqual(localTransportName, property.PropertyType.FullName);
            }
        }
    }

    // -------------------- reverse HTTP (hub relay) --------------------

    [Fact]
    public async Task ReverseHttp_ChatTurn_EchoRoundTripsFromInstanceAThroughHubToInstanceB()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(new EchoChatClient()));

        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildReverseProfileRoutingAsync(harness, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "hello over relay", ct);

        Assert.Equal("hello over relay", text);
    }

    [Fact]
    public async Task ReverseHttp_ShellInvocation_StreamsOutputThroughHubToInstanceA()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ShellTransportListener());

        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildReverseProfileRoutingAsync(harness, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        await using var shell = new ShellOverTransport(transport, ShellEchoRequest("relayed-shell-hello"));
        await shell.OpenAsync(ct);

        var stdout = await ReadUntilFirstMatchAsync(shell.Stream, "relayed-shell-hello", ct);

        Assert.Contains("relayed-shell-hello", stdout);
    }

    [Fact]
    public async Task ReverseHttp_AgentSession_PersistsMessagesInInstanceBStore()
    {
        var ct = TransportScenarioSupport.TestToken();
        var store = AgentPersistenceStoreFactory.CreateInMemory();
        const string sessionId = "reverse-cross-instance-session";

        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(new PersistingEchoChatClient(store, sessionId)));

        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileFactory = await BuildReverseProfileRoutingAsync(harness, ct);

        await using var transport = await ConnectToInstanceBAsync(profileFactory, ct);
        using var client = new ChatClientOverTransport(transport, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "persist over relay", ct);

        Assert.Equal("persist over relay", text);

        var persisted = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = sessionId }, ct);

        Assert.Equal(2, persisted.Length);
        Assert.Equal("persist over relay", persisted[0].Text);
        Assert.Equal("persist over relay", persisted[1].Text);
    }

    // -------------------- profile-routing setup --------------------

    // Instance A's TransportFactoryRegistry hosts the ForwardHttpInstance itself, so profile routing
    // hands back the production HttpTransport paired with the executor listeners.
    private static async Task<ProfileRouting> BuildForwardProfileRoutingAsync(
        ForwardHttpInstance forward,
        CancellationToken ct)
    {
        var instanceBConnectionDescriptor = ParseJson(
            """{"type":"http","url":"https://instance-b.example/"}""");
        var profiles = await SeedTwoInstanceProfilesAsync(ct);

        var innerRegistry = new TransportFactoryRegistry();
        innerRegistry.Register(forward);

        var session = new WorkspaceEntitySession
        {
            UserEntityId = SharedUserId,
            ComputerEntityId = InstanceAComputerId,
            UserComputerProfileEntityId = InstanceAProfileId,
        };

        return new ProfileRouting(
            new UserComputerProfileTransportFactory(
                profiles,
                session,
                innerRegistry,
                trustedTransientRoutesEnabled: true),
            instanceBConnectionDescriptor);
    }

    // Instance A's reverse-HTTP wiring: reuse the harness to build a real
    // ReverseHttpForwardingTransportFactory (over the in-process hub shim), then route via
    // UserComputerProfileTransportFactory using Instance B's {"type":"reverse-http",...} descriptor.
    private static async Task<ProfileRouting> BuildReverseProfileRoutingAsync(
        HubRelayHarness harness,
        CancellationToken ct)
    {
        var instanceBConnectionDescriptor = ParseJson(
            $$"""{"type":"reverse-http","hub-urls":["{{HubRelayHarness.DefaultHubUrl}}"],"entity-id":"{{harness.ExecutorEntityId:D}}"}""");
        var profiles = await SeedTwoInstanceProfilesAsync(ct);

        var innerRegistry = new TransportFactoryRegistry();
        innerRegistry.Register(harness.CreateForwardingFactory());

        var session = new WorkspaceEntitySession
        {
            UserEntityId = SharedUserId,
            ComputerEntityId = InstanceAComputerId,
            UserComputerProfileEntityId = InstanceAProfileId,
        };

        return new ProfileRouting(
            new UserComputerProfileTransportFactory(
                profiles,
                session,
                innerRegistry,
                trustedTransientRoutesEnabled: true),
            instanceBConnectionDescriptor);
    }

    private static async Task<ITransport> ConnectToInstanceBAsync(
        ProfileRouting profileRouting,
        CancellationToken ct)
    {
        var descriptor = BuildProfileDescriptor(profileRouting.ConnectionDescriptor);
        var transport = await profileRouting.Factory.ConnectToAsync(descriptor, ct);
        Assert.NotNull(transport);
        return transport!;
    }

    private static async Task<IDataAccessLayer> SeedTwoInstanceProfilesAsync(CancellationToken ct)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(ct);
        await fixture.SeedManyValidAsync(
            [
                BuildNamedEntity(SharedUserId, "user", ["users", "by-id", SharedUserId.ToString()]),
                BuildNamedEntity(InstanceAComputerId, "computer", ["computers", "by-id", InstanceAComputerId.ToString()]),
                BuildNamedEntity(InstanceBComputerId, "computer", ["computers", "by-id", InstanceBComputerId.ToString()]),
                BuildProfileEntity(InstanceAProfileId, InstanceAComputerId),
                BuildProfileEntity(InstanceBProfileId, InstanceBComputerId),
            ],
            ct);
        return fixture.DataAccessLayer;
    }

    private static JsonElement BuildProfileEntity(
        EntityId profileId,
        EntityId computerId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", profileId.Value.ToString());
            writer.WriteStartArray("entity-types");
            writer.WriteStringValue("entity");
            writer.WriteStringValue("user-computer-profile");
            writer.WriteEndArray();
            writer.WriteStartArray("computer-reference");
            writer.WriteStringValue("computers");
            writer.WriteStringValue("by-id");
            writer.WriteStringValue(computerId.Value.ToString());
            writer.WriteEndArray();
            writer.WriteStartArray("user-reference");
            writer.WriteStringValue("users");
            writer.WriteStringValue("by-id");
            writer.WriteStringValue(SharedUserId.Value.ToString());
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement BuildNamedEntity(
        EntityId entityId,
        string entityType,
        IReadOnlyList<string> name)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", entityId.ToString());
            writer.WriteStartArray("entity-types");
            writer.WriteStringValue("entity");
            writer.WriteStringValue(entityType);
            writer.WriteEndArray();
            writer.WriteStartArray("names");
            writer.WriteStartArray();
            foreach (var component in name)
            {
                writer.WriteStringValue(component);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement BuildProfileDescriptor(JsonElement connectionDescriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "user-computer-profile");
            writer.WriteString("entity-id", InstanceBProfileId.ToString());
            writer.WritePropertyName("connection-descriptor");
            connectionDescriptor.WriteTo(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private sealed record ProfileRouting(
        UserComputerProfileTransportFactory Factory,
        JsonElement ConnectionDescriptor);

    // -------------------- helpers --------------------

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ShellEchoRequest(string payload)
    {
        // "cmd /c echo <payload>" is the smallest cross-Windows shell round-trip that both the
        // forward-HTTP and reverse-HTTP scenarios can exercise without depending on external tooling.
        var json = OperatingSystem.IsWindows()
            ? $$"""{"type":"shell","command":"cmd","args":["/c","echo","{{payload}}"]}"""
            : $$"""{"type":"shell","command":"/bin/sh","args":["-c","echo {{payload}}"]}""";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<string> ReadUntilFirstMatchAsync(Stream stream, string marker, CancellationToken ct)
    {
        var buffer = new byte[512];
        var accumulated = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (accumulated.ToString().Contains(marker, StringComparison.Ordinal))
            {
                return accumulated.ToString();
            }
        }

        return accumulated.ToString();
    }

    // Real forward-HTTP wire: PairedWebSocket plumbs a production HttpTransport against a
    // production ServerHttpTransport, which hosts the executor listeners. Doubles as an
    // ITransportFactory so Instance A's registry can route B's descriptor here without a
    // LocalTransport leaf substitute (the removal of which is asserted in the guard tests).
    internal sealed class ForwardHttpInstance : IAsyncDisposable, ITransportFactory
    {
        public const string DescriptorType = "in-process-forward-http";

        private readonly InProcessHttpServerTransportFactory httpServer;
        private readonly PairedWebSocket clientSocket;
        private readonly PairedWebSocket serverSocket;
        private readonly ServerHttpTransport server;
        private readonly HttpTransport client;
        private readonly Task serverRun;

        private ForwardHttpInstance(
            InProcessHttpServerTransportFactory httpServer,
            PairedWebSocket clientSocket,
            PairedWebSocket serverSocket,
            ServerHttpTransport server,
            HttpTransport client,
            Task serverRun)
        {
            this.httpServer = httpServer;
            this.clientSocket = clientSocket;
            this.serverSocket = serverSocket;
            this.server = server;
            this.client = client;
            this.serverRun = serverRun;
        }

        public ITransport ClientTransport => this.client;

        public InProcessHttpServerTransportFactory HttpServer => this.httpServer;

        public static async Task<ForwardHttpInstance> CreateAsync(TransportRegistry executorRegistry, CancellationToken ct)
        {
            var (clientSocket, serverSocket) = PairedWebSocket.CreatePair();
            var server = new ServerHttpTransport(serverSocket, executorRegistry, TimeSpan.FromHours(1));
            var serverRun = Task.Run(() => server.RunAsync(CancellationToken.None), CancellationToken.None);
            var client = new HttpTransport(clientSocket, TimeSpan.FromHours(1));
            var httpServer = new InProcessHttpServerTransportFactory();
            await httpServer.AcceptAsync(client, ct).ConfigureAwait(false);
            return new ForwardHttpInstance(httpServer, clientSocket, serverSocket, server, client, serverRun);
        }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (!connectionDescriptor.TryGetProperty("type", out var type)
                || (type.GetString() is not { } descriptorType
                    || (descriptorType != DescriptorType && descriptorType != "http")))
            {
                return Task.FromResult<ITransport?>(null);
            }

            return Task.FromResult<ITransport?>(new NonOwningTransport(this.client));
        }

        public async ValueTask DisposeAsync()
        {
            await this.httpServer.DisposeAsync().ConfigureAwait(false);
            await this.server.DisposeAsync().ConfigureAwait(false);
            try
            {
                await this.serverRun.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
            }

            this.clientSocket.Dispose();
            this.serverSocket.Dispose();
        }

        // Prevents `await using` at each test's transport-open site from tearing down the shared
        // forward-HTTP transport (ForwardHttpInstance owns lifecycle). The unwrapped inner is
        // exposed for the LeafWire_IsRealHttpTransport guard test.
        internal sealed class NonOwningTransport : ITransport
        {
            private readonly ITransport inner;

            public NonOwningTransport(ITransport inner) => this.inner = inner;

            public ITransport Inner => this.inner;

            public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
                => this.inner.ConnectToMessageChannelAsync(request, ct);

            public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
                => this.inner.ConnectToStreamAsync(request, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Persists (user, assistant) turn pairs to the executor-side IAgentPersistenceStore after the
    // real EchoChatClient produces its echoed reply. Models a full agent-stack turn executing on
    // Instance B where Instance A never touches the persistence store directly.
    private sealed class PersistingEchoChatClient : IChatClient
    {
        private readonly EchoChatClient inner = new();
        private readonly IAgentPersistenceStore store;
        private readonly string sessionId;

        public PersistingEchoChatClient(IAgentPersistenceStore store, string sessionId)
        {
            this.store = store;
            this.sessionId = sessionId;
        }

        public void Dispose() => this.inner.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => this.inner.GetService(serviceType, serviceKey);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetResponseAsync(messages, options, cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var incoming = messages.ToArray();
            var assistantText = new StringBuilder();
            await foreach (var update in this.inner.GetStreamingResponseAsync(incoming, options, cancellationToken)
                .ConfigureAwait(false))
            {
                assistantText.Append(update.Text);
                yield return update;
            }

            var toPersist = new List<ChatMessage>(incoming)
            {
                new(ChatRole.Assistant, assistantText.ToString()),
            };
            await this.store.StoreAsync(
                new StoreRequestAgent
                {
                    Agent = new PersistedAgent { AgentSessionId = this.sessionId },
                    NewMessages = toPersist.ToArray(),
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}

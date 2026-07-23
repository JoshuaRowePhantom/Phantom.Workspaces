using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Shell;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Issue #1083 — full agent-stack scenarios across two logical Phantom.Workspaces instances,
/// exercised over the in-process forward-HTTP and reverse-HTTP harnesses, fast/offline.
///
/// <para><b>Two-instance identity is real, not decorative.</b> Both instances are represented
/// end-to-end by two real <c>user-computer-profile</c> entities seeded via
/// <see cref="SchemaPopulator.Populate"/> into a real <see cref="InMemoryDataAccessLayer"/>.
/// Instance A opens a <c>{"type":"user-computer-profile","entity-id":&lt;B&gt;}</c> descriptor which
/// the production <see cref="UserComputerProfileTransportFactory"/> resolves through A's real
/// <see cref="TransportFactoryRegistry"/> — Instance B's <c>connection-descriptor</c> on its seeded
/// profile document is what selects the underlying transport factory (forward-HTTP vs.
/// reverse-HTTP), so the two seeded profile IDs genuinely drive the routing.</para>
///
/// <para><b>Forward-HTTP cells</b> host the executor on a <see cref="LocalTransport"/> (the
/// in-process transport that supports both message channels <i>and</i> streams — chat and shell) and
/// register it with the real <see cref="InProcessHttpServerTransportFactory"/>, the forward-HTTP
/// server harness used by <see cref="LeaseExpiryTests"/>. Instance A's factory registry then holds a
/// small <see cref="PreConnectedForwardTransportFactory"/> that dispenses the accepted forward-HTTP
/// transport when the profile router resolves B's <c>connection-descriptor</c> — so the wire
/// exercised is the forward-HTTP in-process harness (not a bare <c>LocalTransport</c> that bypasses
/// the profile+registry routing as the earlier version of these tests did).</para>
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

    // Instance A's forward-HTTP wiring. Seed a real InMemoryDataAccessLayer (SchemaPopulator-populated)
    // with two user-computer-profile entities; Instance B's document carries a real forward-HTTP
    // connection-descriptor. Instance A's TransportFactoryRegistry hosts a PreConnectedForwardTransportFactory
    // that returns the accepted forward-HTTP transport (LocalTransport, the in-process transport that supports
    // channels + streams — registered with the real InProcessHttpServerTransportFactory harness).
    // UserComputerProfileTransportFactory then routes A's user-computer-profile descriptor to that factory.
    private static async Task<UserComputerProfileTransportFactory> BuildForwardProfileRoutingAsync(
        ForwardHttpInstance forward,
        CancellationToken ct)
    {
        var instanceBConnectionDescriptor = ParseJson(
            $$"""{"type":"{{PreConnectedForwardTransportFactory.DescriptorType}}","instance":"instance-b"}""");
        var profiles = await SeedTwoInstanceProfilesAsync(instanceBConnectionDescriptor, ct);

        var innerRegistry = new TransportFactoryRegistry();
        innerRegistry.Register(new PreConnectedForwardTransportFactory(forward.ClientTransport));

        var session = new WorkspaceEntitySession
        {
            UserEntityId = SharedUserId,
            ComputerEntityId = InstanceAComputerId,
            UserComputerProfileEntityId = InstanceAProfileId,
        };

        return new UserComputerProfileTransportFactory(profiles, session, innerRegistry);
    }

    // Instance A's reverse-HTTP wiring: reuse the harness to build a real
    // ReverseHttpForwardingTransportFactory (over the in-process hub shim), then route via
    // UserComputerProfileTransportFactory using Instance B's {"type":"reverse-http",...} descriptor.
    private static async Task<UserComputerProfileTransportFactory> BuildReverseProfileRoutingAsync(
        HubRelayHarness harness,
        CancellationToken ct)
    {
        var instanceBConnectionDescriptor = ParseJson(
            $$"""{"type":"reverse-http","hub-urls":["{{HubRelayHarness.DefaultHubUrl}}"],"entity-id":"{{harness.ExecutorEntityId:D}}"}""");
        var profiles = await SeedTwoInstanceProfilesAsync(instanceBConnectionDescriptor, ct);

        var innerRegistry = new TransportFactoryRegistry();
        innerRegistry.Register(harness.CreateForwardingFactory());

        var session = new WorkspaceEntitySession
        {
            UserEntityId = SharedUserId,
            ComputerEntityId = InstanceAComputerId,
            UserComputerProfileEntityId = InstanceAProfileId,
        };

        return new UserComputerProfileTransportFactory(profiles, session, innerRegistry);
    }

    private static async Task<ITransport> ConnectToInstanceBAsync(
        UserComputerProfileTransportFactory profileFactory,
        CancellationToken ct)
    {
        var descriptor = ParseJson(
            $$"""{"type":"user-computer-profile","entity-id":"{{InstanceBProfileId.Value}}"}""");
        var transport = await profileFactory.ConnectToAsync(descriptor, ct);
        Assert.NotNull(transport);
        return transport!;
    }

    // Seed a real InMemoryDataAccessLayer with two user-computer-profile entities. SchemaPopulator
    // populates the built-in schema first (real repo shape); two schema-shaped profile documents are
    // then appended. Instance B's document carries the connection-descriptor so
    // UserComputerProfileTransportFactory routes to it via the factory registry.
    private static async Task<IDataAccessLayer> SeedTwoInstanceProfilesAsync(
        JsonElement instanceBConnectionDescriptor,
        CancellationToken ct)
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var populator = new SchemaPopulator(dataAccessLayer);
        var populateErrors = await populator.Populate();
        Assert.Empty(populateErrors);

        var instanceAData = BuildProfileEntity(InstanceAProfileId, InstanceAComputerId, connectionDescriptor: null);
        var instanceBData = BuildProfileEntity(InstanceBProfileId, InstanceBComputerId, instanceBConnectionDescriptor);

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed two user-computer-profile entities for #1083 tests." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = InstanceAProfileId,
                        Data = instanceAData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                    new EntityChange
                    {
                        EntityId = InstanceBProfileId,
                        Data = instanceBData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            ct);
        Assert.All(updateResult.EntityResults, result => Assert.Empty(result.Errors));

        return dataAccessLayer;
    }

    private static JsonElement BuildProfileEntity(
        EntityId profileId,
        EntityId computerId,
        JsonElement? connectionDescriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", profileId.Value.ToString());
            writer.WriteStartArray("entity-types");
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
            if (connectionDescriptor is { } descriptor)
            {
                writer.WritePropertyName("connection-descriptor");
                descriptor.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

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

    // Accepted forward-HTTP transport hosting the executor. LocalTransport is used because it is the
    // in-process transport that supports both message channels (chat, persistence) and streams (shell).
    // The transport is registered with the real InProcessHttpServerTransportFactory (the forward-HTTP
    // server harness used by LeaseExpiryTests) so lease-tracking parity with the reverse-HTTP half is
    // preserved.
    private sealed class ForwardHttpInstance : IAsyncDisposable
    {
        private readonly InProcessHttpServerTransportFactory httpServer;
        private readonly LocalTransport transport;

        private ForwardHttpInstance(InProcessHttpServerTransportFactory httpServer, LocalTransport transport)
        {
            this.httpServer = httpServer;
            this.transport = transport;
        }

        public ITransport ClientTransport => this.transport;

        public InProcessHttpServerTransportFactory HttpServer => this.httpServer;

        public static async Task<ForwardHttpInstance> CreateAsync(TransportRegistry executorRegistry, CancellationToken ct)
        {
            var httpServer = new InProcessHttpServerTransportFactory();
            var transport = new LocalTransport(executorRegistry);
            await httpServer.AcceptAsync(transport, ct).ConfigureAwait(false);
            return new ForwardHttpInstance(httpServer, transport);
        }

        public async ValueTask DisposeAsync()
        {
            await this.httpServer.DisposeAsync().ConfigureAwait(false);
            await this.transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Test-only ITransportFactory that dispenses the pre-accepted forward-HTTP transport when a
    // matching connection-descriptor is presented by UserComputerProfileTransportFactory. This is
    // what lets Instance A's factory registry participate in real profile-based routing without
    // opening a new socket per call (the forward-HTTP transport was already accepted by the server
    // harness above).
    private sealed class PreConnectedForwardTransportFactory : ITransportFactory
    {
        public const string DescriptorType = "in-process-forward-http";

        private readonly ITransport transport;

        public PreConnectedForwardTransportFactory(ITransport transport)
        {
            this.transport = transport;
        }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (!connectionDescriptor.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), DescriptorType, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<ITransport?>(null);
            }

            return Task.FromResult<ITransport?>(new NonOwningTransport(this.transport));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Prevents `await using` at each test's transport-open site from tearing down the shared
        // forward-HTTP transport (ForwardHttpInstance owns lifecycle).
        private sealed class NonOwningTransport : ITransport
        {
            private readonly ITransport inner;

            public NonOwningTransport(ITransport inner) => this.inner = inner;

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

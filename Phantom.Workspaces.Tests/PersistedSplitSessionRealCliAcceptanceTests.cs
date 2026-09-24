using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Tests;

public sealed class PersistedSplitSessionRealCliAcceptanceTests
{
    private static readonly EntityId SourceProfile =
        new("10101010-1010-4010-8010-101010101598");
    private static readonly EntityId DaemonProfile =
        new("dadadada-dada-4ada-8ada-dadadada1598");
    private static readonly EntityId ShadeProfile =
        new("5ade5ade-5ade-4ade-8ade-5ade5ade1598");
    private static readonly EntityId UserId =
        new("15981598-1598-4598-8598-159815981598");
    private static readonly EntityId ManifestId =
        new("f5d851ca-0b5d-4466-a1f7-f73d026b21a1");
    private static readonly EntityId HubProfile =
        new("15981598-aaaa-4aaa-8aaa-159815981598");
    private const string SessionId =
        "f5d851ca0b5d4466a1f7f73d026b21a1-acceptance";

    [Fact]
    [Trait("Category", "WebView")]
    public async Task PersistedSplitSession_RealByokCli_DaemonToShade_ProjectsAgentResultsAndReconnects()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(80));
        var ct = timeout.Token;
        await using var server = new ScriptedByokChatServer();
        var conversation = server.AddConversation(
            "daemon-to-shade",
            request => request.AnyMessageContains("user", "opening-query"));
        EnqueueSessionToolCall(conversation, "session-tool-first");
        EnqueueTextResponse(conversation, "daemon-shade-first-result");
        EnqueueSessionToolCall(conversation, "session-tool-restored");
        var restoredResponse = conversation.Client.EnqueueStreamingResponse();
        restoredResponse.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "daemon-shade-restored-result"));
        var restoredResponseGate = restoredResponse.Complete(isReady: false);
        EnqueueTextResponse(conversation, "daemon-shade-spare");

        await using var fixture = await PersistedSplitFixture.CreateAsync(
            server.BaseUrl,
            ct);
        try
        {
            await using var sourceLease = await fixture.AttachFromSourceAsync(ct);
            var sourceChat = Assert.IsType<RemoteAgentChat>(sourceLease.AgentChat);
            await using var ownerLease = await fixture.OwnerFactory.GetAsync(
                new AgentSessionId(SessionId),
                registerAsRunningAgent: false,
                ct);
            var ownerChat = Assert.IsType<AgentChat>(ownerLease.AgentChat);

            var firstProjected = WaitForAssistantAsync(
                sourceChat,
                "daemon-shade-first-result");
            var firstStreaming = WaitForRunningItemsAsync(sourceChat, nonEmpty: true);
            var firstPersisted = WaitForPersistedAsync(ownerChat);
            var firstResult = await EnqueueAtSourceAsync(
                sourceChat,
                "opening-query-visible",
                ct);
            await firstStreaming.WaitAsync(ct);
            var sourceFirstIdle = WaitForRunningItemsAsync(sourceChat, nonEmpty: false);
            var sourceFirstNotBusy = WaitForBusyAsync(sourceChat, busy: false);
            var ownerFirstIdle = WaitForRunningItemsAsync(ownerChat, nonEmpty: false);
            await Task.WhenAll(
                firstProjected,
                sourceFirstIdle,
                sourceFirstNotBusy,
                ownerFirstIdle,
                firstPersisted).WaitAsync(ct);

            Assert.Equal(AgentInputQueueCommandStatus.Applied, firstResult.Status);
            AssertProjectedTurn(
                sourceChat,
                "opening-query-visible",
                "daemon-shade-first-result");
            Assert.Empty(sourceChat.RunningItems);
            Assert.False(sourceChat.IsBusy);
            Assert.Empty(ownerChat.RunningItems);
            var firstToolResult = await conversation.GetRequestAsync(1).WaitAsync(ct);
            Assert.Contains(SessionId, firstToolResult.Body, StringComparison.Ordinal);
            Assert.Contains(
                DaemonProfile.ToString(),
                firstToolResult.Body,
                StringComparison.Ordinal);
            await AssertPersistedTurnAsync(
                fixture.OwnerPersistence,
                "opening-query-visible",
                "daemon-shade-first-result",
                ct);

            var secondProjected = WaitForAssistantAsync(
                sourceChat,
                "daemon-shade-restored-result");
            var secondStreaming = WaitForRunningItemsAsync(sourceChat, nonEmpty: true);
            var ownerSecond = WaitForAssistantAsync(
                ownerChat,
                "daemon-shade-restored-result");
            var secondPersisted = WaitForPersistedAsync(ownerChat);
            var reconnect = fixture.SourceTransport.BlockNextConnection();
            var disconnected = WaitForConnectionAsync(sourceChat, connected: false);
            var reconnected = WaitForConnectionAsync(sourceChat, connected: true);
            var secondResult = await EnqueueAtSourceAsync(
                sourceChat,
                "opening-query-after-reconnect",
                ct);
            await conversation.GetRequestAsync(3).WaitAsync(ct);
            await secondStreaming.WaitAsync(ct);
            var sourceSecondIdle = WaitForRunningItemsAsync(sourceChat, nonEmpty: false);
            var sourceSecondNotBusy = WaitForBusyAsync(sourceChat, busy: false);
            await fixture.SourceTransport.DropCurrentChannelAsync();
            await disconnected.WaitAsync(ct);
            await reconnect.Started.WaitAsync(ct);
            restoredResponseGate.MarkReady();
            await Task.WhenAll(ownerSecond, secondPersisted).WaitAsync(ct);
            Assert.DoesNotContain(
                sourceChat.History,
                item => ContainsText(item, "daemon-shade-restored-result"));

            reconnect.Release();
            await Task.WhenAll(
                reconnected,
                secondProjected,
                sourceSecondIdle,
                sourceSecondNotBusy).WaitAsync(ct);

            Assert.Equal(AgentInputQueueCommandStatus.Applied, secondResult.Status);
            AssertProjectedTurn(
                sourceChat,
                "opening-query-after-reconnect",
                "daemon-shade-restored-result");
            Assert.Empty(sourceChat.RunningItems);
            Assert.False(sourceChat.IsBusy);
            Assert.True(sourceChat.IsConnected);
            Assert.Single(
                fixture.SourceTable.RunningSessions,
                row => row.SessionId == new AgentSessionId(SessionId)
                    && row.IsRemote
                    && row.IsConnected);
            await AssertPersistedTurnAsync(
                fixture.OwnerPersistence,
                "opening-query-after-reconnect",
                "daemon-shade-restored-result",
                ct);

            var shadeRequest = await fixture.ShadeRequest.WaitAsync(ct);
            Assert.Equal(
                CopilotSessionTransportFrames.ConnectionType,
                shadeRequest.GetProperty("type").GetString());
            Assert.Equal(
                DaemonProfile.ToString(),
                fixture.ShadeCaller?.UserComputerProfileEntityId);
            Assert.Contains(
                fixture.SourceRouting.Descriptors,
                descriptor => IsProfileDescriptor(descriptor, DaemonProfile));
            Assert.Contains(
                fixture.DaemonRouting.Descriptors,
                descriptor => IsProfileDescriptor(descriptor, ShadeProfile));
            Assert.DoesNotContain(
                fixture.DaemonRouting.Descriptors,
                descriptor => IsDescriptorType(descriptor, "local")
                    || IsDescriptorType(descriptor, "http"));
            Assert.Equal(4, server.RecordedRequests.Count);
            Assert.Empty(server.Failures);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"Persisted split-session acceptance timed out. "
                + $"Server requests={server.RecordedRequests.Count}, "
                + $"server failures={server.Failures.Count}, "
                + $"source routes={fixture.SourceRouting.Descriptors.Count}, "
                + $"daemon routes={fixture.DaemonRouting.Descriptors.Count}, "
                + $"shade accepted={fixture.ShadeRequest.IsCompleted}.");
            throw;
        }
    }

    private static async Task<AgentInputQueueCommandResult> EnqueueAtSourceAsync(
        RemoteAgentChat chat,
        string prompt,
        CancellationToken cancellationToken)
        => await chat.InputQueues.EnqueueAsync(
            new EnqueueAgentInputRequest
            {
                TargetQueueId = chat.InputQueues.DefaultQueue.Snapshot.QueueId,
                ExpectedRevision = chat.InputQueues.Snapshot.Revision,
                Messages = [new ChatMessage(ChatRole.User, prompt)],
                CommandId = Guid.NewGuid(),
            },
            cancellationToken);

    private static void EnqueueSessionToolCall(
        ConversationClient conversation,
        string callId)
    {
        var response = conversation.Client.EnqueueStreamingResponse();
        response.EnqueueUpdate(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    callId,
                    "get_current_session",
                    new Dictionary<string, object?>
                    {
                        ["include_profile"] = false,
                        ["include_definition"] = false,
                    })]));
        response.Complete();
    }

    private static void EnqueueTextResponse(
        ConversationClient conversation,
        string text)
    {
        var response = conversation.Client.EnqueueStreamingResponse();
        response.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, text));
        response.Complete();
    }

    private static async Task AssertPersistedTurnAsync(
        InMemoryAgentPersistenceStore store,
        string prompt,
        string answer,
        CancellationToken cancellationToken)
    {
        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = SessionId },
            cancellationToken);
        Assert.Contains(
            messages,
            message => message.Role == ChatRole.User
                && message.Contents.OfType<TextContent>().Any(
                    content => content.Text == prompt));
        Assert.Contains(
            messages,
            message => message.Role == ChatRole.Assistant
                && message.Contents.OfType<TextContent>().Any(
                    content => content.Text == answer));
    }

    private static void AssertProjectedTurn(
        RemoteAgentChat chat,
        string prompt,
        string answer)
    {
        var history = chat.History.ToArray();
        var diagnostic = string.Join(
            Environment.NewLine,
            history.Select(
                item => $"{item.Role}: "
                    + string.Join(
                        " | ",
                        item.Contents.Select(
                            content => $"{content.GetType().Name}={content}"))));
        Assert.True(
            history.Any(
                item => item.Role == ChatRole.User
                    && ContainsText(item, prompt)),
            diagnostic);
        Assert.True(
            history.Any(
                item => item.Role == ChatRole.Assistant
                    && ContainsText(item, answer)),
            diagnostic);
    }

    private static bool ContainsText(AgentChatHistoryItem item, string text)
        => item.Contents.OfType<TextContent>().Any(
            content => content.Text.Contains(text, StringComparison.Ordinal));

    private static Task WaitForAssistantAsync(IAgentChat chat, string text)
    {
        if (chat.History.Any(item => ContainsText(item, text)))
            return Task.CompletedTask;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.TurnCompleted += OnCompleted;
        return completion.Task;

        void OnCompleted(object? sender, AgentChatHistoryItem item)
        {
            if (!ContainsText(item, text))
                return;
            chat.TurnCompleted -= OnCompleted;
            completion.TrySetResult();
        }
    }

    private static Task WaitForPersistedAsync(AgentChat chat)
    {
        var middleware = Assert.IsType<StreamingPersistenceMiddleware>(
            chat.GetService(typeof(StreamingPersistenceMiddleware)));
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        middleware.MessagePersisted += OnPersisted;
        return completion.Task;

        void OnPersisted(ChatMessage message)
        {
            _ = message;
            middleware.MessagePersisted -= OnPersisted;
            completion.TrySetResult();
        }
    }

    private static Task WaitForRunningItemsAsync(IAgentChat chat, bool nonEmpty)
    {
        if ((chat.RunningItems.Count > 0) == nonEmpty)
            return Task.CompletedTask;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = (INotifyCollectionChanged)chat.RunningItems;
        collection.CollectionChanged += OnChanged;
        return completion.Task;

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if ((chat.RunningItems.Count > 0) != nonEmpty)
                return;
            collection.CollectionChanged -= OnChanged;
            completion.TrySetResult();
        }
    }

    private static Task WaitForConnectionAsync(RemoteAgentChat chat, bool connected)
    {
        if (chat.IsConnected == connected)
            return Task.CompletedTask;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.RuntimeStateChanged += OnChanged;
        return completion.Task;

        void OnChanged(object? sender, EventArgs args)
        {
            if (chat.IsConnected != connected)
                return;
            chat.RuntimeStateChanged -= OnChanged;
            completion.TrySetResult();
        }
    }

    private static Task WaitForBusyAsync(RemoteAgentChat chat, bool busy)
    {
        if (chat.IsBusy == busy)
            return Task.CompletedTask;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.RuntimeStateChanged += OnChanged;
        return completion.Task;

        void OnChanged(object? sender, EventArgs args)
        {
            if (chat.IsBusy != busy)
                return;
            chat.RuntimeStateChanged -= OnChanged;
            completion.TrySetResult();
        }
    }

    private static bool IsProfileDescriptor(JsonElement descriptor, EntityId profile)
        => IsDescriptorType(descriptor, "user-computer-profile")
            && descriptor.TryGetProperty("entity-id", out var id)
            && id.GetString() == profile.ToString();

    private static bool IsDescriptorType(JsonElement descriptor, string type)
        => descriptor.ValueKind == JsonValueKind.Object
            && descriptor.TryGetProperty("type", out var value)
            && value.GetString() == type;

    private sealed class PersistedSplitFixture : IAsyncDisposable
    {
        private readonly TaskCompletionSource<AgentSessionAttachFailure> attachFailure;
        private readonly AgentChatFactory sourceFactory;
        private readonly AgentChatFactory ownerFactory;
        private readonly WorkspacesTransportComposition daemonComposition;
        private readonly ReverseExecutionDispatcher daemonDispatcher;
        private readonly HubRelayHarness hub;
        private readonly IdentityRecordingListener shadeListener;

        private PersistedSplitFixture(
            InMemoryAgentPersistenceStore ownerPersistence,
            AgentChatFactory sourceFactory,
            RunningAgentChatTable sourceTable,
            RecordingTransportFactoryRegistry sourceRouting,
            AgentChatFactory ownerFactory,
            WorkspacesTransportComposition daemonComposition,
            RecordingTransportFactoryRegistry daemonRouting,
            ReverseExecutionDispatcher daemonDispatcher,
            HubRelayHarness hub,
            IdentityRecordingListener shadeListener,
            TaskCompletionSource<AgentSessionAttachFailure> attachFailure,
            TrackingTransport sourceTransport,
            JsonElement sessionEntity)
        {
            this.OwnerPersistence = ownerPersistence;
            this.sourceFactory = sourceFactory;
            this.SourceTable = sourceTable;
            this.SourceRouting = sourceRouting;
            this.ownerFactory = ownerFactory;
            this.OwnerFactory = ownerFactory;
            this.daemonComposition = daemonComposition;
            this.DaemonRouting = daemonRouting;
            this.daemonDispatcher = daemonDispatcher;
            this.hub = hub;
            this.shadeListener = shadeListener;
            this.attachFailure = attachFailure;
            this.SourceTransport = sourceTransport;
            this.SessionEntity = sessionEntity;
        }

        internal InMemoryAgentPersistenceStore OwnerPersistence { get; }
        internal AgentChatFactory OwnerFactory { get; }
        internal RunningAgentChatTable SourceTable { get; }
        internal RecordingTransportFactoryRegistry SourceRouting { get; }
        internal RecordingTransportFactoryRegistry DaemonRouting { get; }
        internal TrackingTransport SourceTransport { get; }
        internal JsonElement SessionEntity { get; }
        internal Task<JsonElement> ShadeRequest => this.shadeListener.RequestReceived;
        internal TransportPeerIdentity? ShadeCaller => this.shadeListener.ObservedIdentity;

        internal static async Task<PersistedSplitFixture> CreateAsync(
            string providerBaseUrl,
            CancellationToken cancellationToken)
        {
            _ = CopilotCliLocator.FindOrThrow();
            var shadeIdentities = new TransportPeerIdentityProvider();
            var shadeListener = new IdentityRecordingListener(
                new CopilotClientTransportListener(
                    new AgentServices
                    {
                        LoggerFactory = new SafeLoggerFactory(),
                        RemoteCopilotProviderResolver = new FixedProviderResolver(
                            new ProviderConfig
                            {
                                Type = "openai",
                                BaseUrl = providerBaseUrl,
                                ApiKey = "shade-test-key",
                                WireApi = "chat-completions",
                                ModelId = "gpt-test",
                            }),
                    }),
                shadeIdentities);
            var shadeRegistry = new TransportRegistry();
            shadeRegistry.Register(shadeListener);
            var hub = await HubRelayHarness.CreateAsync(
                shadeRegistry,
                cancellationToken,
                shadeIdentities,
                executorEntityId: ShadeProfile.Value);

            var seedFixture = await ValidatingEntitySeedFixture.CreateAsync(
                cancellationToken);
            var data = seedFixture.DataAccessLayer;
            var sessionEntity = await SeedAsync(
                seedFixture,
                providerBaseUrl,
                HubRelayHarness.DefaultHubUrl,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var unboundContext = new CurrentSessionContext
            {
                AgentSessionId = "unbound",
                OwningProfileEntityId = DaemonProfile.ToString(),
                OwnershipGeneration = 0,
            };
            var hostServices = new AgentServices
            {
                LoggerFactory = new SafeLoggerFactory(),
                ToolsetFactory = Phantom.Workspaces.Llm.ToolsetFactory
                    .CreateCurrentSessionToolsetFactory(
                        data,
                        unboundContext,
                        Phantom.Workspaces.Llm.ToolsetFactory.CreateDefaultToolsetFactory()),
                ToolResourceFactory = Phantom.Workspaces.Services.ToolResourceFactory
                    .CreateMcpServerResolution(data, "acceptance-user", "DAEMON"),
            };
            var ownerPersistence = new InMemoryAgentPersistenceStore();
            var ownerFactory = new AgentChatFactory(
                ownerPersistence,
                hostServices,
                TaskScheduler.Default);
            var daemonRouting = new RecordingTransportFactoryRegistry();
            var ownerTable = new RunningAgentChatTable(
                ownerFactory,
                new AgentSessionRuntimeContextFactory(daemonRouting));
            var attachFailure = new TaskCompletionSource<AgentSessionAttachFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var daemonComposition = new WorkspacesTransportComposition(
                data,
                WorkspaceSession(DaemonProfile),
                hostServices.LoggerFactory!,
                agentServices: hostServices,
                registryProvider: new TransportFactoryRegistryProvider(daemonRouting),
                runningAgentChats: ownerTable);
            daemonComposition.AgentSessionListener!.AttachFailed +=
                failure => attachFailure.TrySetResult(failure);
            daemonRouting.Inner = CreateProfileRegistry(
                data,
                WorkspaceSession(DaemonProfile),
                hub.CreateForwardingFactory(Peer(DaemonProfile)));

            await hub.Fixture.SimulateClientRegistrationAsync(
                DaemonProfile.Value,
                cancellationToken);
            var daemonDispatcher = new ReverseExecutionDispatcher(
                hub.Fixture.LastClientRegistrationChannel!,
                daemonComposition.LocalListeners,
                daemonComposition.AgentSessionPeerIdentities);

            var sourceFactory = new AgentChatFactory(
                new InMemoryAgentPersistenceStore(),
                new AgentServices(),
                TaskScheduler.Default);
            var sourceRouting = new RecordingTransportFactoryRegistry();
            var sourceTable = new RunningAgentChatTable(
                sourceFactory,
                new AgentSessionRuntimeContextFactory(sourceRouting));
            sourceRouting.Inner = CreateProfileRegistry(
                data,
                WorkspaceSession(SourceProfile),
                hub.CreateForwardingFactory(Peer(SourceProfile)));
            var sourceTransport = new TrackingTransport(
                await sourceRouting.ConnectToAsync(
                    ProfileDescriptor(DaemonProfile),
                    cancellationToken));
            return new PersistedSplitFixture(
                ownerPersistence,
                sourceFactory,
                sourceTable,
                sourceRouting,
                ownerFactory,
                daemonComposition,
                daemonRouting,
                daemonDispatcher,
                hub,
                shadeListener,
                attachFailure,
                sourceTransport,
                sessionEntity);
        }

        internal async Task<RunningAgentChatLease> AttachFromSourceAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.SourceTable.AcquireAsync(
                new AcquireAgentChatRequest
                {
                    AgentSessionId = new AgentSessionId(SessionId),
                    AgentSessionEntity = this.SessionEntity,
                    AgentServices = new AgentServices(),
                    ForegroundScheduler = TaskScheduler.Default,
                    EntityName = "f5d851ca acceptance",
                    EntityId = this.SessionEntity.GetProperty("entity-id").GetString(),
                    AcquisitionMode = AgentChatAcquisitionMode.StartOrAttachRemote,
                    OwningProfileTransport = this.SourceTransport,
                },
                cancellationToken);
            }
            catch (RemoteAgentSessionException exception)
                when (this.attachFailure.Task.IsCompletedSuccessfully)
            {
                var failure = await this.attachFailure.Task;
                throw new InvalidOperationException(
                    $"Attach failed at '{failure.Stage}' "
                    + $"({failure.Category}, {failure.Error.GetType().Name})."
                    + Environment.NewLine
                    + failure.Error,
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this.daemonDispatcher.DisposeAsync();
            await this.daemonComposition.DisposeAsync();
            await this.sourceFactory.DisposeAsync();
            await this.ownerFactory.DisposeAsync();
            await this.hub.DisposeAsync();
        }

        private static ITransportFactoryRegistry CreateProfileRegistry(
            IDataAccessLayer data,
            WorkspaceEntitySession session,
            ITransportFactory forwarding)
        {
            var routes = new TransportFactoryRegistry();
            routes.Register(forwarding);
            var registry = new TransportFactoryRegistry();
            registry.Register(
                new UserComputerProfileTransportFactory(
                    data,
                    session,
                    routes,
                    reachabilityRouteStore: new DataAccessReachabilityRouteStore(data)));
            return registry;
        }

        private static async Task<JsonElement> SeedAsync(
            ValidatingEntitySeedFixture fixture,
            string providerBaseUrl,
            string hubUrl,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var sessionData = AgentSessionEntityFactory.CreateEntityData(
                new CreateAgentSessionEntityDataRequest
                {
                    AgentDefinitionEntityId = ManifestId,
                    AgentDisplayName = "GitHub Copilot (split executor)",
                    AgentSessionId = SessionId,
                    AgentSessionNames =
                    [
                        new EntityName("tests", "agent-sessions", SessionId),
                    ],
                    CurrentTime = now,
                    ComputerName = "DAEMON",
                    HostProfileEntityId = DaemonProfile,
                    ParameterValues = new Dictionary<string, string>
                    {
                        ["working-directory"] = Path.GetTempPath(),
                    },
                    ParameterSelections = new Dictionary<string, JsonElement>
                    {
                        ["worker-profile"] = JsonSerializer.SerializeToElement(
                            new Dictionary<string, string>
                            {
                                ["user-computer-profile"] = ShadeProfile.ToString(),
                            }),
                    },
                    SessionExecutor = ExecutorBindings.LocalDescriptor(),
                    ExecutorComponentBindings = JsonSerializer.SerializeToElement(
                        new Dictionary<string, JsonElement>
                        {
                            ["worker"] = ProfileDescriptor(ShadeProfile),
                        }),
                });
            var manifest = Json($$"""
                {
                  "entity-id": "{{ManifestId}}",
                  "entity-types": ["entity", "agent-manifest"],
                  "names": [["tests", "agent-manifests", "f5d851ca-acceptance"]],
                  "display-name": {"default": "F5D persisted split acceptance"},
                  "manifest": {
                    "name": "f5d-persisted-split",
                    "displayName": "F5D persisted split",
                    "parameters": {
                      "properties": [
                        {"name": "working-directory", "kind": "string", "required": true}
                      ]
                    },
                    "template": {
                      "kind": "prompt",
                      "name": "f5d-persisted-split",
                      "model": {
                        "id": "gpt-test",
                        "provider": "openai",
                        "connection": {
                          "kind": "key",
                          "endpoint": {{JsonSerializer.Serialize(providerBaseUrl)}},
                          "apiKey": "daemon-caller-key"
                        },
                        "options": {
                          "additionalProperties": {
                            "executor": "worker",
                            "remoteProvider": "shade-byok",
                            "working-directory": "${working-directory}"
                          }
                        }
                      },
                      "instructions": "Use the current-session tool when requested.",
                      "tools": []
                    },
                    "resources": [
                      {
                        "kind": "executor",
                        "id": "parameter",
                        "name": "worker",
                        "options": {"parameter": "worker-profile"}
                      },
                      {"kind": "tool", "id": "fixed", "name": "current-session"}
                    ]
                  }
                }
                """);
            var documents = new List<JsonElement>
            {
                UserEntity(),
                ComputerEntity(SourceProfile, "SOURCE"),
                ComputerEntity(DaemonProfile, "DAEMON"),
                ComputerEntity(ShadeProfile, "SHADE"),
                Profile(SourceProfile, "SOURCE", hubUrl, now),
                Profile(DaemonProfile, "DAEMON", hubUrl, now),
                Profile(ShadeProfile, "SHADE", hubUrl, now),
                manifest,
                sessionData,
            };
            await fixture.SeedManyValidAsync(documents, cancellationToken);
            return sessionData;
        }

        private static JsonElement UserEntity()
            => Json($$"""
                {
                  "entity-id": "{{UserId}}",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "username", "acceptance"]]
                }
                """);

        private static JsonElement ComputerEntity(
            EntityId profile,
            string computerName)
            => Json($$"""
                {
                  "entity-id": "{{ComputerId(profile)}}",
                  "entity-types": ["entity", "computer"],
                  "names": [["computers", "hostname", "{{computerName}}"]]
                }
                """);

        private static JsonElement Profile(
            EntityId id,
            string computerName,
            string hubUrl,
            DateTimeOffset now)
            => Json($$"""
                {
                  "entity-id": "{{id}}",
                  "entity-types": ["entity", "user-computer-profile"],
                  "names": [["profiles", "{{computerName}}"]],
                  "computer-reference": ["computers", "hostname", "{{computerName}}"],
                  "user-reference": ["users", "username", "acceptance"],
                  "display-name": {"default": "{{computerName}}"},
                  "reachability": {
                    "routes": {
                      "reverse-http:{{HubProfile}}": {
                        "descriptor": {
                          "type": "reverse-http",
                          "hub-urls": [{{JsonSerializer.Serialize(hubUrl)}}],
                          "entity-id": "{{id}}"
                        },
                        "owner-profile-entity-id": "{{id}}",
                        "priority": 100,
                        "last-confirmed": "{{now:O}}",
                        "expires-at": "{{now.AddMinutes(10):O}}"
                      }
                    }
                  }
                }
                """);
    }

    private sealed class RecordingTransportFactoryRegistry
        : ITransportFactoryRegistry
    {
        private readonly ConcurrentQueue<JsonElement> descriptors = new();
        internal ITransportFactoryRegistry? Inner { get; set; }
        internal IReadOnlyList<JsonElement> Descriptors => [.. this.descriptors];

        public void Register(ITransportFactory factory)
        {
        }

        public async Task<ITransport> ConnectToAsync(
            JsonElement connectionDescriptor,
            CancellationToken ct = default)
        {
            this.descriptors.Enqueue(connectionDescriptor.Clone());
            return await (this.Inner
                    ?? throw new InvalidOperationException(
                        "Transport registry is not initialized."))
                .ConnectToAsync(connectionDescriptor, ct);
        }
    }

    private sealed class TrackingTransport(ITransport inner) : ITransport
    {
        private readonly object gate = new();
        private IMessageChannel? currentChannel;
        private ConnectionGate? nextConnection;

        internal ConnectionGate BlockNextConnection()
        {
            lock (this.gate)
            {
                if (this.nextConnection is not null)
                    throw new InvalidOperationException(
                        "A connection is already blocked.");
                this.nextConnection = new ConnectionGate();
                return this.nextConnection;
            }
        }

        internal async ValueTask DropCurrentChannelAsync()
        {
            IMessageChannel? channel;
            lock (this.gate)
                channel = this.currentChannel;
            if (channel is null)
                throw new InvalidOperationException(
                    "No source attachment channel is connected.");
            await channel.DisposeAsync();
        }

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            ConnectionGate? blocked;
            lock (this.gate)
            {
                blocked = this.nextConnection;
                this.nextConnection = null;
            }
            if (blocked is not null)
            {
                blocked.MarkStarted();
                await blocked.WaitForReleaseAsync(ct);
            }
            var channel = await inner.ConnectToMessageChannelAsync(request, ct);
            lock (this.gate)
                this.currentChannel = channel;
            return channel;
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => inner.ConnectToStreamAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        internal sealed class ConnectionGate
        {
            private readonly TaskCompletionSource started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource released =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Started => this.started.Task;
            internal void MarkStarted() => this.started.TrySetResult();
            internal void Release() => this.released.TrySetResult();
            internal Task WaitForReleaseAsync(CancellationToken cancellationToken)
                => this.released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FixedProviderResolver(ProviderConfig provider)
        : IRemoteCopilotProviderResolver
    {
        public Task<ProviderConfig?> ResolveAsync(
            string providerReference,
            string modelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ProviderConfig?>(
                providerReference == "shade-byok"
                    && modelId == "gpt-test"
                    ? provider
                    : null);
        }
    }

    private sealed class IdentityRecordingListener(
        ITransportListener inner,
        TransportPeerIdentityProvider identities) : ITransportListener
    {
        private readonly TaskCompletionSource<JsonElement> requestReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<JsonElement> RequestReceived => this.requestReceived.Task;
        internal TransportPeerIdentity? ObservedIdentity { get; private set; }

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            if (CopilotSessionTransportFrames.IsConnectionRequest(request))
            {
                this.ObservedIdentity = identities.GetRequiredIdentity(channel);
                this.requestReceived.TrySetResult(request.Clone());
            }
            return await inner.OnChannelOpenAsync(request, channel, ct);
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(
            JsonElement request,
            Stream stream,
            CancellationToken ct = default)
            => inner.OnStreamOpenAsync(request, stream, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SafeLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new SafeLogger();
        public void AddProvider(ILoggerProvider provider)
        {
        }
        public void Dispose()
        {
        }

        private sealed class SafeLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    private static TransportPeerIdentity Peer(EntityId profile)
        => new()
        {
            AuthenticationScheme = "test",
            StablePeerId = profile.ToString(),
            UserEntityId = UserId.ToString(),
            UserComputerProfileEntityId = profile.ToString(),
        };

    private static WorkspaceEntitySession WorkspaceSession(EntityId profile)
        => new()
        {
            UserEntityId = UserId,
            ComputerEntityId = ComputerId(profile),
            UserComputerProfileEntityId = profile,
        };

    private static EntityId ComputerId(EntityId profile)
    {
        var bytes = profile.Value.ToByteArray();
        bytes[0] ^= 0x5a;
        return new EntityId(new Guid(bytes));
    }

    private static JsonElement ProfileDescriptor(EntityId profile)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["type"] = "user-computer-profile",
                ["entity-id"] = profile.ToString(),
            });

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}

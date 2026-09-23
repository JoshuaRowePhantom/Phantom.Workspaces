using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using AgentSchema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;

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
        var conversation = server.AddConversation("daemon-to-shade", _ => true);
        EnqueueSessionToolCall(conversation, "session-tool-first");
        EnqueueTextResponse(conversation, "daemon-shade-first-result");
        EnqueueSessionToolCall(conversation, "session-tool-restored");
        var restoredResponse = conversation.Client.EnqueueStreamingResponse();
        restoredResponse.EnqueueUpdate(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                "daemon-shade-restored-result"));
        var restoredResponseGate = restoredResponse.Complete(isReady: false);
        EnqueueTextResponse(conversation, "daemon-shade-spare");

        await using var hub = await RealHttpHub.CreateAsync(ct);
        await using var shade = await RemoteSplitWorkerProcess.StartAsync(
            new RemoteSplitWorkerConfiguration(
                hub.Url,
                ShadeProfile.ToString(),
                "shade-real-cli",
                "shade-byok",
                "openai",
                server.BaseUrl,
                "shade-test-key",
                "chat-completions",
                "gpt-test"),
            ct);
        await hub.WaitForRegistrationAsync(ShadeProfile, ct);
        await using var fixture = await PersistedSplitFixture.CreateAsync(
            server.BaseUrl,
            hub,
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
            var firstPersisted = WaitForPersistedTextAsync(
                ownerChat);

            var firstQueueResult = await EnqueueAtSourceAsync(
                sourceChat,
                "opening-query-visible",
                ct);
            await firstStreaming.WaitAsync(ct);
            var sourceFirstIdle = WaitForRunningItemsAsync(
                sourceChat,
                nonEmpty: false);
            var sourceFirstNotBusy = WaitForBusyAsync(
                sourceChat,
                busy: false);
            var ownerFirstIdle = WaitForRunningItemsAsync(
                ownerChat,
                nonEmpty: false);
            await firstProjected.WaitAsync(ct);
            await sourceFirstIdle.WaitAsync(ct);
            await sourceFirstNotBusy.WaitAsync(ct);
            await ownerFirstIdle.WaitAsync(ct);
            await firstPersisted.WaitAsync(ct);

            Assert.Equal(
                AgentInputQueueCommandStatus.Applied,
                firstQueueResult.Status);
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
            var secondStreaming = WaitForRunningItemsAsync(
                sourceChat,
                nonEmpty: true);
            var ownerSecond = WaitForAssistantAsync(
                ownerChat,
                "daemon-shade-restored-result");
            var secondPersisted = WaitForPersistedTextAsync(
                ownerChat);
            var reconnect = fixture.SourceTransport.BlockNextConnection();
            var disconnected = WaitForConnectionAsync(sourceChat, connected: false);
            var reconnected = WaitForConnectionAsync(sourceChat, connected: true);

            var secondQueueResult = await EnqueueAtSourceAsync(
                sourceChat,
                "opening-query-after-reconnect",
                ct);
            await conversation.GetRequestAsync(3).WaitAsync(ct);
            await secondStreaming.WaitAsync(ct);
            var sourceSecondIdle = WaitForRunningItemsAsync(
                sourceChat,
                nonEmpty: false);
            var sourceSecondNotBusy = WaitForBusyAsync(
                sourceChat,
                busy: false);
            await fixture.SourceTransport.DropCurrentChannelAsync();
            await disconnected.WaitAsync(ct);
            await reconnect.Started.WaitAsync(ct);
            restoredResponseGate.MarkReady();
            await ownerSecond.WaitAsync(ct);
            await secondPersisted.WaitAsync(ct);

            Assert.DoesNotContain(
                sourceChat.History,
                item => ContainsText(
                    item,
                    "daemon-shade-restored-result"));
            reconnect.Release();
            await reconnected.WaitAsync(ct);
            await secondProjected.WaitAsync(ct);
            await sourceSecondIdle.WaitAsync(ct);
            await sourceSecondNotBusy.WaitAsync(ct);

            Assert.Equal(
                AgentInputQueueCommandStatus.Applied,
                secondQueueResult.Status);
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

            var workerRequest = await shade.RequestReceived.WaitAsync(ct);
            Assert.Equal(
                "shade-real-cli",
                workerRequest.GetProperty("worker").GetString());
            Assert.Equal(
                DaemonProfile.ToString(),
                workerRequest.GetProperty("caller").GetString());
            Assert.Equal(
                "copilot-sdk-session",
                workerRequest.GetProperty("requestType").GetString());
            Assert.Contains(
                fixture.SourceRouting.Descriptors,
                descriptor => IsProfileDescriptor(
                    descriptor,
                    DaemonProfile));
            Assert.Contains(
                fixture.DaemonRouting.Descriptors,
                descriptor => IsProfileDescriptor(
                    descriptor,
                    ShadeProfile));
            Assert.DoesNotContain(
                fixture.DaemonRouting.Descriptors,
                descriptor => IsDescriptorType(descriptor, "local")
                    || IsDescriptorType(descriptor, "http"));
            Assert.Equal(4, server.RecordedRequests.Count);
            Assert.Empty(server.Failures);
            Assert.All(shade.OutputLines, line =>
            {
                Assert.DoesNotContain(
                    server.BaseUrl,
                    line,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "shade-test-key",
                    line,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "opening-query",
                    line,
                    StringComparison.Ordinal);
            });
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"Persisted split-session acceptance timed out. "
                + $"Server requests={server.RecordedRequests.Count}, "
                + $"server failures={server.Failures.Count}, "
                + $"source routes={fixture.SourceRouting.Descriptors.Count}, "
                + $"daemon routes={fixture.DaemonRouting.Descriptors.Count}."
                + Environment.NewLine
                + string.Join(Environment.NewLine, shade.OutputLines));
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
                TargetQueueId =
                    chat.InputQueues.DefaultQueue.Snapshot.QueueId,
                ExpectedRevision =
                    chat.InputQueues.Snapshot.Revision,
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
        response.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, text));
        response.Complete();
    }

    private static async Task AssertPersistedTurnAsync(
        InMemoryAgentPersistenceStore store,
        string prompt,
        string answer,
        CancellationToken cancellationToken)
    {
        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest
            {
                AgentSessionId = SessionId,
            },
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
        var diagnostic = RenderHistory(chat.History);
        Assert.True(
            chat.History.Any(
                item => item.Role == ChatRole.User
                    && ContainsText(item, prompt)),
            diagnostic);
        Assert.True(
            chat.History.Any(
                item => item.Role == ChatRole.Assistant
                    && ContainsText(item, answer)),
            diagnostic);
    }

    private static bool ContainsText(
        AgentChatHistoryItem item,
        string text)
        => item.Contents.OfType<TextContent>().Any(
            content => content.Text.Contains(
                text,
                StringComparison.Ordinal));

    private static string RenderHistory(
        IEnumerable<AgentChatHistoryItem> history)
        => string.Join(
            Environment.NewLine,
            history.Select(
                item => $"{item.Role}: "
                    + string.Join(
                        " | ",
                        item.Contents.Select(
                            content => $"{content.GetType().Name}={content}"))));

    private static Task WaitForAssistantAsync(
        IAgentChat chat,
        string text)
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

    private static Task WaitForPersistedTextAsync(
        AgentChat chat)
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

    private static Task WaitForRunningItemsAsync(
        IAgentChat chat,
        bool nonEmpty)
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

    private static Task WaitForConnectionAsync(
        RemoteAgentChat chat,
        bool connected)
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

    private static Task WaitForBusyAsync(
        RemoteAgentChat chat,
        bool busy)
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

    private static bool IsProfileDescriptor(
        JsonElement descriptor,
        EntityId profile)
        => IsDescriptorType(descriptor, "user-computer-profile")
            && descriptor.TryGetProperty("entity-id", out var id)
            && id.GetString() == profile.ToString();

    private static bool IsDescriptorType(
        JsonElement descriptor,
        string type)
        => descriptor.ValueKind == JsonValueKind.Object
            && descriptor.TryGetProperty("type", out var value)
            && value.GetString() == type;

    private sealed class PersistedSplitFixture : IAsyncDisposable
    {
        private readonly AgentChatFactory sourceFactory;
        private readonly WorkspacesTransportComposition sourceComposition;
        private readonly AgentChatFactory ownerFactory;
        private readonly WorkspacesTransportComposition daemonComposition;
        private readonly ReverseHttpClientTransportFactory daemonRegistration;
        private readonly ReverseExecutionDispatcher daemonDispatcher;

        private PersistedSplitFixture(
            InMemoryDataAccessLayer data,
            InMemoryAgentPersistenceStore ownerPersistence,
            AgentChatFactory sourceFactory,
            RunningAgentChatTable sourceTable,
            WorkspacesTransportComposition sourceComposition,
            RecordingTransportFactoryRegistry sourceRouting,
            AgentChatFactory ownerFactory,
            WorkspacesTransportComposition daemonComposition,
            RecordingTransportFactoryRegistry daemonRouting,
            ReverseHttpClientTransportFactory daemonRegistration,
            ReverseExecutionDispatcher daemonDispatcher,
            TrackingTransport sourceTransport,
            JsonElement sessionEntity)
        {
            this.Data = data;
            this.OwnerPersistence = ownerPersistence;
            this.sourceFactory = sourceFactory;
            this.SourceTable = sourceTable;
            this.sourceComposition = sourceComposition;
            this.SourceRouting = sourceRouting;
            this.ownerFactory = ownerFactory;
            this.OwnerFactory = ownerFactory;
            this.daemonComposition = daemonComposition;
            this.DaemonRouting = daemonRouting;
            this.daemonRegistration = daemonRegistration;
            this.daemonDispatcher = daemonDispatcher;
            this.SourceTransport = sourceTransport;
            this.SessionEntity = sessionEntity;
        }

        internal InMemoryDataAccessLayer Data { get; }
        internal InMemoryAgentPersistenceStore OwnerPersistence { get; }
        internal AgentChatFactory OwnerFactory { get; }
        internal RunningAgentChatTable SourceTable { get; }
        internal RecordingTransportFactoryRegistry SourceRouting { get; }
        internal RecordingTransportFactoryRegistry DaemonRouting { get; }
        internal TrackingTransport SourceTransport { get; }
        internal JsonElement SessionEntity { get; }

        internal static async Task<PersistedSplitFixture> CreateAsync(
            string providerBaseUrl,
            RealHttpHub hub,
            CancellationToken cancellationToken)
        {
            var data = new InMemoryDataAccessLayer();
            var now = DateTimeOffset.UtcNow;
            var sessionEntity = await SeedAsync(
                data,
                providerBaseUrl,
                hub.Url,
                now,
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
                ToolsetFactory =
                    Phantom.Workspaces.Llm.ToolsetFactory
                        .CreateCurrentSessionToolsetFactory(
                            data,
                            unboundContext,
                            Phantom.Workspaces.Llm.ToolsetFactory
                                .CreateDefaultToolsetFactory()),
                ToolResourceFactory =
                    Phantom.Workspaces.Services.ToolResourceFactory
                        .CreateMcpServerResolution(
                            data,
                            "acceptance-user",
                            "DAEMON"),
            };
            var ownerPersistence =
                new InMemoryAgentPersistenceStore();
            var ownerFactory = new AgentChatFactory(
                ownerPersistence,
                hostServices,
                TaskScheduler.Default);
            var daemonRouting = new RecordingTransportFactoryRegistry();
            var ownerTable = new RunningAgentChatTable(
                ownerFactory,
                AgentSessionRuntimeContextFactory.FromProvider(
                    new TransportFactoryRegistryProvider(
                        daemonRouting)));
            var daemonComposition = new WorkspacesTransportComposition(
                data,
                WorkspaceSession(DaemonProfile),
                agentServices: hostServices,
                registryProvider:
                    new TransportFactoryRegistryProvider(
                        daemonRouting),
                runningAgentChats: ownerTable);
            daemonRouting.Inner = daemonComposition.TransportFactoryRegistry;

            var daemonRegistration =
                new ReverseHttpClientTransportFactory(
                    hub.Url,
                    DaemonProfile.ToString());
            var daemonChannel =
                await daemonRegistration.EnsureRegisteredAsync(
                    cancellationToken);
            await hub.WaitForRegistrationAsync(
                DaemonProfile,
                cancellationToken);
            var daemonDispatcher = new ReverseExecutionDispatcher(
                daemonChannel,
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
            var sourceComposition = new WorkspacesTransportComposition(
                data,
                WorkspaceSession(SourceProfile),
                registryProvider:
                    new TransportFactoryRegistryProvider(sourceRouting),
                runningAgentChats: sourceTable);
            sourceRouting.Inner = sourceComposition.TransportFactoryRegistry;

            var sourceTransport = new TrackingTransport(
                await sourceRouting.ConnectToAsync(
                    ProfileDescriptor(DaemonProfile),
                    cancellationToken));
            return new PersistedSplitFixture(
                data,
                ownerPersistence,
                sourceFactory,
                sourceTable,
                sourceComposition,
                sourceRouting,
                ownerFactory,
                daemonComposition,
                daemonRouting,
                daemonRegistration,
                daemonDispatcher,
                sourceTransport,
                sessionEntity);
        }

        internal async Task<RunningAgentChatLease> AttachFromSourceAsync(
            CancellationToken cancellationToken)
            => await this.SourceTable.AcquireAsync(
                new AcquireAgentChatRequest
                {
                    AgentSessionId = new AgentSessionId(SessionId),
                    AgentSessionEntity = this.SessionEntity,
                    AgentServices = new AgentServices(),
                    ForegroundScheduler = TaskScheduler.Default,
                    EntityName = "f5d851ca acceptance",
                    EntityId =
                        this.SessionEntity
                            .GetProperty("entity-id")
                            .GetString(),
                    AcquisitionMode =
                        AgentChatAcquisitionMode.StartOrAttachRemote,
                    OwningProfileTransport = this.SourceTransport,
                },
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await this.daemonDispatcher.DisposeAsync();
            await this.daemonRegistration.DisposeAsync();
            await this.sourceComposition.DisposeAsync();
            await this.daemonComposition.DisposeAsync();
            await this.sourceFactory.DisposeAsync();
            await this.ownerFactory.DisposeAsync();
        }

        private static async Task<JsonElement> SeedAsync(
            InMemoryDataAccessLayer data,
            string providerBaseUrl,
            string hubUrl,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var sessionData =
                AgentSessionEntityFactory.CreateEntityData(
                    new CreateAgentSessionEntityDataRequest
                    {
                        AgentDefinitionEntityId = ManifestId,
                        AgentDisplayName =
                            "GitHub Copilot (split executor)",
                        AgentSessionId = SessionId,
                        AgentSessionNames =
                        [
                            new EntityName(
                                "tests",
                                "agent-sessions",
                                SessionId),
                        ],
                        CurrentTime = now,
                        ComputerName = "DAEMON",
                        HostProfileEntityId = DaemonProfile,
                        ParameterValues =
                            new Dictionary<string, string>
                            {
                                ["working-directory"] =
                                    Path.GetTempPath(),
                            },
                        ParameterSelections =
                            new Dictionary<string, JsonElement>
                            {
                                ["worker-profile"] =
                                    JsonSerializer.SerializeToElement(
                                        new Dictionary<string, string>
                                        {
                                            ["user-computer-profile"] =
                                                ShadeProfile.ToString(),
                                        }),
                            },
                        SessionExecutor =
                            ExecutorBindings.LocalDescriptor(),
                        ExecutorComponentBindings =
                            JsonSerializer.SerializeToElement(
                                new Dictionary<string, JsonElement>
                                {
                                    ["worker"] =
                                        ProfileDescriptor(
                                            ShadeProfile),
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
                        {
                          "name": "working-directory",
                          "kind": "string",
                          "required": true
                        }
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
                      {
                        "kind": "tool",
                        "id": "fixed",
                        "name": "current-session"
                      }
                    ]
                  }
                }
                """);
            var changes = new List<EntityChange>
            {
                Change(UserEntity()),
                Change(Profile(SourceProfile, "SOURCE", hubUrl, now)),
                Change(Profile(DaemonProfile, "DAEMON", hubUrl, now)),
                Change(Profile(ShadeProfile, "SHADE", hubUrl, now)),
                Change(manifest),
                new()
                {
                    EntityId = new EntityId(
                        sessionData
                            .GetProperty("entity-id")
                            .GetString()!),
                    Data = sessionData,
                    EntityChangeMode =
                        EntityChangeMode.Replace,
                },
            };
            var result = await data.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown
                        {
                            Text =
                                "Seed persisted split-session acceptance",
                        },
                    },
                    Changes = changes,
                },
                cancellationToken);
            Assert.All(
                result.EntityResults,
                entity => Assert.Empty(entity.Errors));
            return sessionData;
        }

        private static EntityChange Change(JsonElement data)
            => new()
            {
                EntityId = new EntityId(
                    data.GetProperty("entity-id").GetString()!),
                Data = data,
                EntityChangeMode = EntityChangeMode.Replace,
            };

        private static JsonElement UserEntity()
            => Json($$"""
                {
                  "entity-id": "{{UserId}}",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "acceptance"]]
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
                  "user-entity-id": "{{UserId}}",
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
        private readonly ConcurrentQueue<JsonElement> descriptors =
            new();

        internal ITransportFactoryRegistry? Inner { get; set; }

        internal IReadOnlyList<JsonElement> Descriptors =>
            [.. this.descriptors];

        public void Register(ITransportFactory factory)
        {
        }

        public async Task<ITransport> ConnectToAsync(
            JsonElement connectionDescriptor,
            CancellationToken ct = default)
        {
            this.descriptors.Enqueue(
                connectionDescriptor.Clone());
            return await (this.Inner
                    ?? throw new InvalidOperationException(
                        "Transport registry is not initialized."))
                .ConnectToAsync(connectionDescriptor, ct);
        }
    }

    private sealed class TrackingTransport(ITransport inner)
        : ITransport
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

        public async Task<IMessageChannel>
            ConnectToMessageChannelAsync(
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

            var channel = await inner
                .ConnectToMessageChannelAsync(request, ct);
            lock (this.gate)
                this.currentChannel = channel;
            return channel;
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => inner.ConnectToStreamAsync(request, ct);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();

        internal sealed class ConnectionGate
        {
            private readonly TaskCompletionSource started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource released =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Started => this.started.Task;

            internal void MarkStarted()
                => this.started.TrySetResult();

            internal void Release()
                => this.released.TrySetResult();

            internal Task WaitForReleaseAsync(
                CancellationToken cancellationToken)
                => this.released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RealHttpHub : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly HttpServerTransportFactory httpServer;
        private readonly ReverseHttpServerTransportFactory reverseServer;
        private readonly ReverseConnectionStatusRegistry statusRegistry;

        private RealHttpHub(
            WebApplication application,
            HttpServerTransportFactory httpServer,
            ReverseHttpServerTransportFactory reverseServer,
            ReverseConnectionStatusRegistry statusRegistry,
            string url)
        {
            this.application = application;
            this.httpServer = httpServer;
            this.reverseServer = reverseServer;
            this.statusRegistry = statusRegistry;
            this.Url = url;
        }

        internal string Url { get; }

        internal static async Task<RealHttpHub> CreateAsync(
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            application.UseWebSockets();
            var registry = new TransportRegistry();
            var statusRegistry =
                new ReverseConnectionStatusRegistry();
            var reverseServer =
                new ReverseHttpServerTransportFactory(
                    statusRegistry,
                    requireAuthenticatedRelays: true);
            registry.Register(reverseServer);
            var httpServer =
                new HttpServerTransportFactory(registry);
            httpServer.Map(application);
            await application.StartAsync(cancellationToken);
            return new RealHttpHub(
                application,
                httpServer,
                reverseServer,
                statusRegistry,
                Assert.Single(application.Urls));
        }

        internal Task WaitForRegistrationAsync(
            EntityId profile,
            CancellationToken cancellationToken)
        {
            if (IsRegistered())
                return Task.CompletedTask;
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            this.statusRegistry.ConnectionsChanged += OnChanged;
            if (IsRegistered())
                Complete();
            return completion.Task.WaitAsync(cancellationToken);

            bool IsRegistered() =>
                this.statusRegistry.GetConnectedInstances().Any(
                    status => status.ClientInstanceId
                        == profile.ToString());

            void OnChanged(object? sender, EventArgs args)
            {
                if (IsRegistered())
                    Complete();
            }

            void Complete()
            {
                this.statusRegistry.ConnectionsChanged -= OnChanged;
                completion.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
            await this.application.StopAsync(
                cancellation.Token);
            await this.httpServer.DisposeAsync();
            await this.reverseServer.DisposeAsync();
            await this.application.DisposeAsync();
        }
    }

    private sealed record RemoteSplitWorkerConfiguration(
        string HubUrl,
        string WorkerEntityId,
        string WorkerMarker,
        string ProviderReference,
        string ProviderType,
        string ProviderBaseUrl,
        string ProviderApiKey,
        string WireApi,
        string ModelId);

    private sealed class RemoteSplitWorkerProcess
        : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task outputPump;
        private readonly Task errorPump;
        private readonly ConcurrentQueue<string> outputLines = new();
        private readonly ConcurrentQueue<string> errorLines = new();
        private readonly TaskCompletionSource ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JsonElement> requestReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private RemoteSplitWorkerProcess(Process process)
        {
            this.process = process;
            this.outputPump = this.ReadOutputAsync();
            this.errorPump = this.ReadErrorsAsync();
        }

        internal Task<JsonElement> RequestReceived =>
            this.requestReceived.Task;
        internal IReadOnlyList<string> OutputLines =>
            [.. this.outputLines];

        internal static async Task<RemoteSplitWorkerProcess>
            StartAsync(
                RemoteSplitWorkerConfiguration configuration,
                CancellationToken cancellationToken)
        {
            _ = CopilotCliLocator.FindOrThrow();
            var executableName = OperatingSystem.IsWindows()
                ? "Phantom.Workspaces.Transport.TestWorker.exe"
                : "Phantom.Workspaces.Transport.TestWorker";
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                "remote-split-worker",
                executableName);
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "The split-session worker is unavailable.",
                    executable);

            var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory =
                        Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }) ?? throw new InvalidOperationException(
                    "Unable to start the split-session worker.");
            var worker = new RemoteSplitWorkerProcess(process);
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(configuration));
            await process.StandardInput.FlushAsync(
                cancellationToken);
            var exited =
                process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(
                worker.ready.Task,
                exited);
            if (completed == exited)
            {
                await exited;
                await worker.outputPump;
                await worker.errorPump;
                throw new InvalidOperationException(
                    "Split-session worker exited before registration: "
                    + string.Join(
                        Environment.NewLine,
                        worker.errorLines));
            }
            await worker.ready.Task.WaitAsync(
                cancellationToken);
            return worker;
        }

        public async ValueTask DisposeAsync()
        {
            if (!this.process.HasExited)
            {
                try
                {
                    await this.process.StandardInput
                        .WriteLineAsync("stop");
                    await this.process.StandardInput.FlushAsync();
                    using var cancellation =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(10));
                    await this.process.WaitForExitAsync(
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    this.process.Kill(
                        entireProcessTree: true);
                    await this.process.WaitForExitAsync();
                }
                catch (IOException)
                    when (this.process.HasExited)
                {
                }
                catch (InvalidOperationException)
                    when (this.process.HasExited)
                {
                }
            }
            await this.outputPump;
            await this.errorPump;
            this.process.Dispose();
        }

        private async Task ReadOutputAsync()
        {
            while (await this.process.StandardOutput.ReadLineAsync()
                   is { } line)
            {
                this.outputLines.Enqueue(line);
                JsonElement item;
                try
                {
                    item = JsonDocument.Parse(line)
                        .RootElement
                        .Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                var type = item.TryGetProperty(
                    "type",
                    out var typeProperty)
                    ? typeProperty.GetString()
                    : null;
                if (type == "ready")
                    this.ready.TrySetResult();
                else if (type == "request")
                    this.requestReceived.TrySetResult(item);
            }
        }

        private async Task ReadErrorsAsync()
        {
            while (await this.process.StandardError.ReadLineAsync()
                   is { } line)
                this.errorLines.Enqueue(line);
        }
    }

    private sealed class SafeLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName)
            => new SafeLogger();

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

    private static WorkspaceEntitySession WorkspaceSession(
        EntityId profile)
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

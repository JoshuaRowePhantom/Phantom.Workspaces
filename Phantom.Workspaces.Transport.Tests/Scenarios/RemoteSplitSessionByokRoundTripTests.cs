using System.Collections.Specialized;
using System.Text.Json;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Issue #1594 reciprocal split-session coverage. The default tests model A, B, and C as separate
/// in-process instances with distinct validated profile identities, factory registries, and SDK
/// clients. They exercise the production AgentChat → executor binding → profile router → reverse
/// hub → Copilot-session transport composition, but do not claim OS-process or BYOK-wire coverage.
/// The WebView-trait test adds the real Copilot CLI and ScriptedByokChatServer acceptance boundary.
/// Every remote assertion includes the target marker and authenticated caller identity so a local
/// fallback cannot accidentally satisfy the scenario.
/// </summary>
public sealed class RemoteSplitSessionByokRoundTripTests
{
    private static readonly EntityId ProfileA = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1594");
    private static readonly EntityId ProfileB = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb1594");
    private static readonly EntityId ProfileC = new("cccccccc-cccc-4ccc-8ccc-cccccccc1594");
    private static readonly EntityId UserId = new("eeeeeeee-eeee-4eee-8eee-eeeeeeee1594");
    private static readonly EntityId HubProfileId = new("99999999-9999-4999-8999-999999991594");
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(30);

    private const string AgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "remote-split-session",
          "model": { "id": "gpt-5", "provider": "github-copilot" },
          "tools": []
        }
        """;

    [Fact]
    public async Task RemoteSplitSession_Byok_AtoB_CompletesRoundTripAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueTextAndIdle("reply-from-worker-b");

        await using var chat = await setup.CreateChatAsync();
        await CompleteTurnAsync(chat, "A-to-B instance-marker:worker-b");

        AssertSuccessfulTurn(setup, chat, "worker-b", "reply-from-worker-b", ProfileA);
        await AssertPersistedAsync(setup, chat, "reply-from-worker-b");
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_BtoA_CompletesReciprocalRoundTripAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileB, ProfileA, "worker-a");
        setup.Session.EnqueueTextAndIdle("reply-from-worker-a");

        await using var chat = await setup.CreateChatAsync();
        await CompleteTurnAsync(chat, "B-to-A instance-marker:worker-a");

        AssertSuccessfulTurn(setup, chat, "worker-a", "reply-from-worker-a", ProfileB);
        await AssertPersistedAsync(setup, chat, "reply-from-worker-a");
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_CtoAtoB_RelaysRoundTripAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileC, ProfileB, "worker-b");
        setup.Session.EnqueueTextAndIdle("B-to-A-to-C");

        await using var chat = await setup.CreateChatAsync();
        await CompleteTurnAsync(chat, "C-to-A-to-B instance-marker:worker-b");

        AssertSuccessfulTurn(setup, chat, "worker-b", "B-to-A-to-C", ProfileC);
        Assert.Contains(
            setup.StatusRegistry.GetConnectedInstances(),
            status => status.ClientInstanceId == setup.Harness.ExecutorEntityId.ToString()
                && status.InFlightCount > 0);
        await AssertPersistedAsync(setup, chat, "B-to-A-to-C");
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_AtoB_ToolCallExecutesOnExecutorAndGuiCallbackReturnsToSource()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "executor-call", ToolName = "shell" },
        });
        setup.Session.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "executor-call",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "executed-on-worker-b" },
            },
        });
        setup.Session.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "gui-call", ToolName = "workspace-gui" },
        });
        setup.Session.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "gui-call",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "returned-from-source-a" },
            },
        });
        setup.Session.EnqueueEvent(new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        await using var chat = await setup.CreateChatAsync();
        await CompleteTurnAsync(chat, "route both tools");

        var contents = chat.History.SelectMany(static item => item.Contents).ToArray();
        Assert.Contains(contents.OfType<FunctionCallContent>(), item => item.CallId == "executor-call" && item.Name == "shell");
        Assert.Contains(contents.OfType<FunctionResultContent>(), item => item.CallId == "executor-call"
            && item.Result?.ToString()?.Contains("worker-b", StringComparison.Ordinal) == true);
        Assert.Contains(contents.OfType<FunctionCallContent>(), item => item.CallId == "gui-call" && item.Name == "workspace-gui");
        Assert.Contains(contents.OfType<FunctionResultContent>(), item => item.CallId == "gui-call"
            && item.Result?.ToString()?.Contains("source-a", StringComparison.Ordinal) == true);
        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_AtoB_SecondQueuedTurnDrainsAfterFirstCompletes()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueTextAndIdle("first-from-worker-b");

        await using var chat = await setup.CreateChatAsync();
        await CompleteTurnAsync(chat, "first");

        setup.Session.EnqueueTextAndIdle("second-from-worker-b");
        await CompleteTurnAsync(chat, "second");

        Assert.Empty(chat.RunningItems);
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "first-from-worker-b"));
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "second-from-worker-b"));
        Assert.Equal(2, setup.Session.SentPrompts.Count);
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_RemoteChannelOpenThrows_SurfacesTerminalErrorAndClearsRunningItems()
    {
        var listener = new ThrowingListener();
        await using var setup = await SplitSessionSetup.CreateWithListenerAsync(ProfileA, ProfileB, listener);
        await using var chat = await setup.CreateChatAsync();

        await CompleteTurnAsync(chat, "throw while opening");
        await CompleteTurnAsync(chat, "retry after rejected open");

        Assert.Empty(chat.RunningItems);
        AssertProviderError(chat, "channel");
        Assert.Equal(2, listener.OpenAttempts);
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_RemoteListenerHangsDuringStartup_TimesOutAndClearsRunningItems()
    {
        var listener = new HangingListener();
        await using var setup = await SplitSessionSetup.CreateWithListenerAsync(ProfileA, ProfileB, listener);
        await using var chat = await setup.CreateChatAsync();

        var completion = chat.EnqueueUserMessageWithCompletion("hang while opening");
        await listener.Opened.Task.WaitAsync(TestToken());
        setup.TimeProvider.Advance(StartupTimeout);
        await completion.Settlement.WaitAsync(TestToken());

        Assert.Empty(chat.RunningItems);
        AssertProviderError(chat, "timed out");
        await listener.Cancelled.Task.WaitAsync(TestToken());
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_ChannelClosesCleanlyBeforeSessionCreated_SurfacesTerminalErrorAndClearsRunningItems()
    {
        var client = new BlockingCreateCopilotClient();
        await using var setup = await SplitSessionSetup.CreateWithClientAsync(ProfileA, ProfileB, client);
        await using var chat = await setup.CreateChatAsync();
        var cleared = RunningItemsClearedAsync(chat);

        chat.EnqueueUserMessage("close before session-created");
        await client.CreateStarted.Task.WaitAsync(TestToken());
        await setup.ChannelAccepted.WaitAsync(TestToken());
        await setup.Harness.CrashExecutorAsync();
        await cleared.WaitAsync(TestToken());

        Assert.Empty(chat.RunningItems);
        AssertProviderError(chat, "closed");
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_TargetDisconnectsMidTurn_SurfacesTerminalErrorAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "partial-from-worker-b", MessageId = "partial" },
        });
        await using var chat = await setup.CreateChatAsync();

        var completion = chat.EnqueueUserMessageWithCompletion("disconnect mid-turn");
        await setup.Session.ReadSentMessageAsync(TestToken());
        await WaitForRunningTextAsync(chat, "partial-from-worker-b");
        await setup.Harness.CrashExecutorAsync();
        await completion.Settlement.WaitAsync(TestToken());

        Assert.Empty(chat.RunningItems);
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text.Contains("partial-from-worker-b")));
        AssertProviderError(chat, "closed");
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_CallerCancellation_SurfacesCancelledStateAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "partial-before-cancel", MessageId = "cancel" },
        });
        await using var chat = await setup.CreateChatAsync();
        var cleared = RunningItemsClearedAsync(chat);

        chat.EnqueueUserMessage("cancel this turn");
        await setup.Session.ReadSentMessageAsync(TestToken());
        await chat.InterruptAsync();
        await cleared.WaitAsync(TestToken());

        Assert.Empty(chat.RunningItems);
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text.Contains("partial-before-cancel")));
    }

    [Fact]
    public async Task RemoteSplitSession_Byok_ProviderStreamEndsWithoutTerminalEvent_SurfacesTerminalErrorAndClearsRunningItems()
    {
        await using var setup = await SplitSessionSetup.CreateAsync(ProfileA, ProfileB, "worker-b");
        setup.Session.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "legitimate-partial", MessageId = "truncated" },
        });
        await using var chat = await setup.CreateChatAsync();

        var completion = chat.EnqueueUserMessageWithCompletion("provider truncates");
        await setup.Session.ReadSentMessageAsync(TestToken());
        await WaitForRunningTextAsync(chat, "legitimate-partial");
        setup.TimeProvider.Advance(TerminalTimeout);
        await completion.Settlement.WaitAsync(TestToken());

        Assert.Empty(chat.RunningItems);
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text.Contains("legitimate-partial")));
        AssertProviderError(chat, "terminal");

        setup.RecoverySession.EnqueueTextAndIdle("retry-after-truncated-provider");
        await CompleteTurnAsync(chat, "retry");
        Assert.Contains(
            chat.History,
            item => item.Contents.OfType<TextContent>().Any(text => text.Text == "retry-after-truncated-provider"));
    }

    [Fact]
    [Trait("Category", "WebView")]
    public async Task RemoteSplitSession_RealByokCli_AtoB_CompletesRoundTripAndClearsRunningItems()
    {
        var ct = TestToken(TimeSpan.FromSeconds(90));
        await using var server = new ScriptedByokChatServer();
        var conversation = server.AddConversation(
            "worker-b",
            request => request.AnyMessageContains("user", "real-byok-worker-b"));
        var stream = conversation.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "real-byok-reply"));
        stream.Complete();

        var definition = AgentDefinitionLoader.LoadAgentFromJson(ByokDefinitionJson(server.BaseUrl));
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new ChatClientTransportListener(async (_, cancellationToken) =>
        {
            var result = await AgentFactory.CreateChatClientAsync(
                definition,
                new AgentServices(),
                cancellationToken: cancellationToken);
            if (result.ChatClient is CopilotSdkChatClient copilot)
            {
                copilot.SetSubAgentDependencies(new StubRunningAgentChatFactory(), new StubSubAgentTable());
            }

            return result.ChatClient;
        }));

        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var machineA = await harness.ConnectMachineBAsync(ct);
        using var remoteClient = new ChatClientOverTransport(machineA, ChatClientRequest(definition));
        var store = new InMemoryAgentPersistenceStore();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = definition,
            ConfiguredStore = store,
            ClientOverride = remoteClient,
            OverrideUseProvidedChatClientAsIs = true,
            DisplayNameOverride = "real BYOK worker B",
            CancellationToken = ct,
        });

        await CompleteTurnAsync(chat, "real-byok-worker-b");

        Assert.Empty(chat.RunningItems);
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text.Contains("real-byok-reply")));
        Assert.Empty(server.Failures);
    }

    private static async Task CompleteTurnAsync(AgentChat chat, string prompt)
    {
        var completion = chat.EnqueueUserMessageWithCompletion(prompt);
        await completion.Settlement.WaitAsync(TestToken());
    }

    private static void AssertSuccessfulTurn(
        SplitSessionSetup setup,
        AgentChat chat,
        string targetMarker,
        string expectedText,
        EntityId expectedCaller)
    {
        Assert.Empty(chat.RunningItems);
        var diagnostics = string.Join(
            Environment.NewLine,
            chat.History
                .SelectMany(static item => item.Contents)
                .Select(static content => content switch
                {
                    TextContent text => text.Text,
                    ErrorContent error => error.Message,
                    _ => content.ToString() ?? content.GetType().FullName ?? "unknown",
                }));
        Assert.True(
            chat.History.Any(
                item => item.Contents.OfType<TextContent>().Any(text => text.Text == expectedText)),
            diagnostics);
        Assert.Contains(setup.Session.SentPrompts, prompt => prompt.Contains(targetMarker, StringComparison.Ordinal));
        Assert.Equal(targetMarker, setup.Session.SessionId);
        Assert.Equal(expectedCaller.ToString(), setup.ObservedCaller?.UserComputerProfileEntityId);
        Assert.DoesNotContain(
            setup.RoutedDescriptors,
            descriptor => descriptor.TryGetProperty("type", out var type) && type.GetString() == "local");
    }

    private static async Task AssertPersistedAsync(SplitSessionSetup setup, AgentChat chat, string expectedText)
    {
        var restored = await setup.Store.RestoreAsync(
            new RestoreRequest { AgentSessionId = chat.AgentSessionId },
            TestToken());
        Assert.NotNull(restored);
        Assert.Equal(setup.Session.SessionId, restored!.Value.CopilotSdkSessionId);
        var messages = await setup.Store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            TestToken());
        Assert.Contains(messages, message => message.Contents.OfType<TextContent>().Any(text => text.Text == expectedText));
    }

    private static void AssertProviderError(AgentChat chat, string expected)
        => Assert.Contains(
            chat.History.SelectMany(static item => item.Contents).OfType<ErrorContent>(),
            error => error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));

    private static Task RunningItemsClearedAsync(AgentChat chat)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += OnChanged;
        return completion.Task;

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.RunningItems.Count == 0)
            {
                ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= OnChanged;
                completion.TrySetResult();
            }
        }
    }

    private static async Task WaitForRunningTextAsync(AgentChat chat, string expectedText)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedItems = new HashSet<INotifyCollectionChanged>();
        var outer = (INotifyCollectionChanged)chat.RunningItems;
        outer.CollectionChanged += OnOuterChanged;
        try
        {
            AttachCurrentItems();
            Check();
            await completion.Task.WaitAsync(TestToken());
        }
        finally
        {
            outer.CollectionChanged -= OnOuterChanged;
            foreach (var item in observedItems)
            {
                item.CollectionChanged -= OnInnerChanged;
            }
        }

        void OnOuterChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            AttachCurrentItems();
            Check();
        }

        void OnInnerChanged(object? sender, NotifyCollectionChangedEventArgs args) => Check();

        void AttachCurrentItems()
        {
            foreach (var runningItem in chat.RunningItems)
            {
                var items = (INotifyCollectionChanged)runningItem.Items;
                if (observedItems.Add(items))
                {
                    items.CollectionChanged += OnInnerChanged;
                }
            }
        }

        void Check()
        {
            if (chat.RunningItems
                .SelectMany(static item => item.Items)
                .SelectMany(static item => item.Contents)
                .OfType<TextContent>()
                .Any(text => text.Text.Contains(expectedText, StringComparison.Ordinal)))
            {
                completion.TrySetResult();
            }
        }
    }

    private static CancellationToken TestToken(TimeSpan? timeout = null)
        => new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30)).Token;

    private static string ByokDefinitionJson(string baseUrl) => $$"""
        {
          "kind": "prompt",
          "name": "remote-real-byok",
          "model": {
            "id": "gpt-test",
            "provider": "github-models",
            "connection": {
              "kind": "key",
              "endpoint": "{{baseUrl}}",
              "apiKey": "test-key"
            },
            "options": {
              "additionalProperties": {
                "cliPath": {{JsonSerializer.Serialize(CopilotCliLocator.FindOrThrow())}}
              }
            }
          },
          "tools": []
        }
        """;

    private static JsonElement ChatClientRequest(AgentDefinition definition)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = definition.ToJson(),
        });

    private sealed class SplitSessionSetup : IAsyncDisposable
    {
        private readonly AgentDefinition definition;
        private readonly CopilotSdkChatClient client;
        private readonly CancellationToken cancellationToken;
        private readonly RecordingTransportFactoryRegistry routingRegistry;
        private readonly StubRunningAgentChatFactory runningAgentChatFactory = new();

        private SplitSessionSetup(
            HubRelayHarness harness,
            ReverseConnectionStatusRegistry statusRegistry,
            FakeTimeProvider timeProvider,
            AgentDefinition definition,
            CopilotSdkChatClient client,
            FakeCopilotSession session,
            FakeCopilotSession recoverySession,
            InMemoryAgentPersistenceStore store,
            TransportPeerIdentity authenticatedCaller,
            RecordingTransportFactoryRegistry routingRegistry,
            IdentityRecordingListener identityListener,
            Task channelAccepted,
            CancellationToken cancellationToken)
        {
            this.Harness = harness;
            this.StatusRegistry = statusRegistry;
            this.TimeProvider = timeProvider;
            this.definition = definition;
            this.client = client;
            this.Session = session;
            this.RecoverySession = recoverySession;
            this.Store = store;
            this.AuthenticatedCaller = authenticatedCaller;
            this.routingRegistry = routingRegistry;
            this.IdentityListener = identityListener;
            this.ChannelAccepted = channelAccepted;
            this.cancellationToken = cancellationToken;
        }

        public HubRelayHarness Harness { get; }

        public ReverseConnectionStatusRegistry StatusRegistry { get; }

        public FakeTimeProvider TimeProvider { get; }

        public FakeCopilotSession Session { get; }

        public FakeCopilotSession RecoverySession { get; }

        public InMemoryAgentPersistenceStore Store { get; }

        public TransportPeerIdentity AuthenticatedCaller { get; }

        public IReadOnlyList<JsonElement> RoutedDescriptors => this.routingRegistry.Descriptors;

        public Task ChannelAccepted { get; }

        private IdentityRecordingListener IdentityListener { get; }

        public TransportPeerIdentity? ObservedCaller => this.IdentityListener.ObservedIdentity;

        public static Task<SplitSessionSetup> CreateAsync(
            EntityId source,
            EntityId target,
            string targetMarker,
            CancellationToken cancellationToken = default)
        {
            var session = new FakeCopilotSession { SessionId = targetMarker };
            var recoverySession = new FakeCopilotSession { SessionId = targetMarker };
            return CreateWithClientAsync(
                source,
                target,
                new SequencedFakeCopilotClient([session, recoverySession]),
                session,
                recoverySession,
                cancellationToken);
        }

        public static Task<SplitSessionSetup> CreateWithClientAsync(
            EntityId source,
            EntityId target,
            ICopilotClient client,
            CancellationToken cancellationToken = default)
            => CreateWithClientAsync(
                source,
                target,
                client,
                new FakeCopilotSession { SessionId = target == ProfileA ? "worker-a" : "worker-b" },
                new FakeCopilotSession { SessionId = target == ProfileA ? "worker-a" : "worker-b" },
                cancellationToken);

        private static async Task<SplitSessionSetup> CreateWithClientAsync(
            EntityId source,
            EntityId target,
            ICopilotClient remoteClient,
            FakeCopilotSession session,
            FakeCopilotSession recoverySession,
            CancellationToken cancellationToken)
        {
            var listener = new CopilotClientTransportListener(new FakeCopilotClientFactory(remoteClient));
            return await CreateCoreAsync(
                source,
                target,
                listener,
                session,
                recoverySession,
                cancellationToken);
        }

        public static Task<SplitSessionSetup> CreateWithListenerAsync(
            EntityId source,
            EntityId target,
            ITransportListener listener,
            CancellationToken cancellationToken = default)
            => CreateCoreAsync(
                source,
                target,
                listener,
                new FakeCopilotSession { SessionId = target == ProfileA ? "worker-a" : "worker-b" },
                new FakeCopilotSession { SessionId = target == ProfileA ? "worker-a" : "worker-b" },
                cancellationToken);

        private static async Task<SplitSessionSetup> CreateCoreAsync(
            EntityId source,
            EntityId target,
            ITransportListener listener,
            FakeCopilotSession session,
            FakeCopilotSession recoverySession,
            CancellationToken cancellationToken)
        {
            var effectiveCancellation = cancellationToken.CanBeCanceled
                ? cancellationToken
                : TestToken();
            var statusRegistry = new ReverseConnectionStatusRegistry();
            var reverseServer = new ReverseHttpServerTransportFactory(
                statusRegistry,
                hubProfileEntityId: HubProfileId);
            var peerIdentities = new TransportPeerIdentityProvider();
            var authenticatedCaller = Peer(source);
            var identityListener = new IdentityRecordingListener(listener, peerIdentities);
            var executorRegistry = new TransportRegistry();
            executorRegistry.Register(identityListener);
            var harness = await HubRelayHarness.CreateAsync(
                executorRegistry,
                effectiveCancellation,
                peerIdentities,
                reverseServer,
                target.Value);
            var timeProvider = new FakeTimeProvider(Now);
            var data = await SeedProfilesAsync(
                target,
                timeProvider.GetUtcNow(),
                effectiveCancellation);

            var innerRegistry = new TransportFactoryRegistry();
            innerRegistry.Register(harness.CreateForwardingFactory(authenticatedCaller));
            var profileFactory = new UserComputerProfileTransportFactory(
                data,
                Session(source),
                innerRegistry,
                reachabilityRouteStore: new DataAccessReachabilityRouteStore(data),
                timeProvider: timeProvider);
            var routingRegistry = new RecordingTransportFactoryRegistry();
            routingRegistry.Register(profileFactory);

            var options = new ModelOptions
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["executor"] = "worker",
                },
            };
            var sdkClient = new CopilotSdkChatClient(
                "gpt-5",
                "remote split session",
                null,
                null,
                modelOptions: options);
            sdkClient.ConfigureExecutorRouting(
                new ExecutorBindings
                {
                    Bindings = new Dictionary<string, JsonElement>
                    {
                        ["worker"] = ProfileDescriptor(target),
                    },
                },
                routingRegistry);
            sdkClient.ConfigureRemoteSessionLifecycleForTest(
                timeProvider,
                StartupTimeout,
                TerminalTimeout);
            sdkClient.SetSubAgentDependencies(new StubRunningAgentChatFactory(), new StubSubAgentTable());

            return new SplitSessionSetup(
                harness,
                statusRegistry,
                timeProvider,
                AgentDefinitionLoader.LoadAgentFromJson(AgentDefinitionJson),
                sdkClient,
                session,
                recoverySession,
                new InMemoryAgentPersistenceStore(),
                authenticatedCaller,
                routingRegistry,
                identityListener,
                identityListener.Accepted.Task,
                effectiveCancellation);
        }

        public Task<AgentChat> CreateChatAsync()
            => AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = this.definition,
                AgentServices = new AgentServices
                {
                    RunningAgentChatFactory = this.runningAgentChatFactory,
                },
                ConfiguredStore = this.Store,
                ClientOverride = this.client,
                OverrideUseProvidedChatClientAsIs = true,
                DisplayNameOverride = "remote split session",
                CancellationToken = this.cancellationToken,
            });

        public async ValueTask DisposeAsync()
        {
            await this.client.DisposeAsync();
            await this.routingRegistry.DisposeAsync();
            await this.Harness.DisposeAsync();
        }
    }

    private static async Task<IDataAccessLayer> SeedProfilesAsync(
        EntityId target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(cancellationToken);
        var documents = new List<JsonElement>
        {
            Json($$"""
                {
                  "entity-id": "{{UserId}}",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "by-id", "{{UserId}}"]]
                }
                """),
        };
        foreach (var profile in new[] { ProfileA, ProfileB, ProfileC })
        {
            var computer = ComputerId(profile);
            documents.Add(Json($$"""
                {
                  "entity-id": "{{computer}}",
                  "entity-types": ["entity", "computer"],
                  "names": [["computers", "by-id", "{{computer}}"]]
                }
                """));
            var reachability = profile == target
                ? $$"""
                  ,"reachability": {
                    "routes": {
                      "reverse-http:{{HubProfileId}}": {
                        "descriptor": {
                          "type": "reverse-http",
                          "hub-urls": ["{{HubRelayHarness.DefaultHubUrl}}"],
                          "entity-id": "{{target}}"
                        },
                        "owner-profile-entity-id": "{{profile}}",
                        "priority": 100,
                        "last-confirmed": "{{now:O}}",
                        "expires-at": "{{now.AddMinutes(5):O}}"
                      }
                    }
                  }
                  """
                : string.Empty;
            documents.Add(Json($$"""
                {
                  "entity-id": "{{profile}}",
                  "entity-types": ["entity", "user-computer-profile"],
                  "computer-reference": ["computers", "by-id", "{{computer}}"],
                  "user-reference": ["users", "by-id", "{{UserId}}"]
                  {{reachability}}
                }
                """));
        }

        await fixture.SeedManyValidAsync(documents, cancellationToken);
        return fixture.DataAccessLayer;
    }

    private static WorkspaceEntitySession Session(EntityId profile)
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

    private static TransportPeerIdentity Peer(EntityId profile)
        => new()
        {
            AuthenticationScheme = "test",
            StablePeerId = profile.ToString(),
            UserEntityId = UserId.ToString(),
            UserComputerProfileEntityId = profile.ToString(),
        };

    private static JsonElement ProfileDescriptor(EntityId target)
        => Json($$"""{"type":"user-computer-profile","entity-id":"{{target}}"}""");

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class RecordingTransportFactoryRegistry : ITransportFactoryRegistry
    {
        private readonly TransportFactoryRegistry inner = new();
        private readonly List<JsonElement> descriptors = [];

        public IReadOnlyList<JsonElement> Descriptors
        {
            get
            {
                lock (this.descriptors)
                {
                    return this.descriptors.ToArray();
                }
            }
        }

        public void Register(ITransportFactory factory) => this.inner.Register(factory);

        public async Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            lock (this.descriptors)
            {
                this.descriptors.Add(connectionDescriptor.Clone());
            }

            return await this.inner.ConnectToAsync(connectionDescriptor, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityRecordingListener(
        ITransportListener inner,
        TransportPeerIdentityProvider identities) : ITransportListener
    {
        public TaskCompletionSource Accepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TransportPeerIdentity? ObservedIdentity { get; private set; }

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            if (request.TryGetProperty("type", out var type)
                && type.GetString() == "copilot-sdk-session")
            {
                this.ObservedIdentity = identities.GetRequiredIdentity(channel);
                Assert.NotNull(this.ObservedIdentity.UserComputerProfileEntityId);
            }

            var lease = await inner.OnChannelOpenAsync(request, channel, ct);
            if (lease is not null)
            {
                this.Accepted.TrySetResult();
            }

            return lease;
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(
            JsonElement request,
            Stream stream,
            CancellationToken ct = default)
            => inner.OnStreamOpenAsync(request, stream, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ThrowingListener : ITransportListener
    {
        public int OpenAttempts { get; private set; }

        public Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            this.OpenAttempts++;
            throw new InvalidOperationException("channel listener rejected the request");
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HangingListener : ITransportListener
    {
        public TaskCompletionSource Opened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            this.Opened.TrySetResult();
            try
            {
                await TaskCompletionSourceWithCancellation(ct);
                throw new InvalidOperationException("Unreachable.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                this.Cancelled.TrySetResult();
                throw;
            }
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task TaskCompletionSourceWithCancellation(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
            return completion.Task;
        }
    }

    private sealed class BlockingCreateCopilotClient : ICopilotClient
    {
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public async Task<ICopilotSession> CreateSessionAsync(
            SessionConfig config,
            CancellationToken cancellationToken)
        {
            this.CreateStarted.TrySetResult();
            var completion = new TaskCompletionSource<ICopilotSession>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public Task<ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
            => this.CreateSessionAsync(new SessionConfig(), cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequencedFakeCopilotClient(IReadOnlyList<FakeCopilotSession> sessions) : ICopilotClient
    {
        private int nextSession;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            var session = sessions[Math.Min(Interlocked.Increment(ref this.nextSession) - 1, sessions.Count - 1)];
            session.OnCreateSession(config);
            return Task.FromResult<ICopilotSession>(session);
        }

        public Task<ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken)
        {
            var session = sessions[Math.Min(Interlocked.Increment(ref this.nextSession) - 1, sessions.Count - 1)];
            session.OnResumeSession(sessionId, config);
            return Task.FromResult<ICopilotSession>(session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubRunningAgentChatFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(
            AgentSessionId sessionId,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubSubAgentTable : ISubAgentTable
    {
        public Task<SubAgent> Add(AgentChat agentChat) => throw new NotSupportedException();
    }
}

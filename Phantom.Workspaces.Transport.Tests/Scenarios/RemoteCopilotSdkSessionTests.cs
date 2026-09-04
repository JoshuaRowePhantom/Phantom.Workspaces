using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// End-to-end integration test for #1319 (epic #1313): a SOURCE <see cref="AgentChat"/> whose
/// <c>IChatClient</c> is a <see cref="ChatClientOverTransport"/> reaches an in-process REMOTE
/// where a <c>CopilotSdkChatClient</c> is built by <see cref="ChatClientTransportListener"/> from
/// the wire-carried <see cref="AgentDefinition"/>, driven by the hermetic scripted Copilot SDK
/// harness (S4 / #1316). The three tests exercise the SDK-scripted turn (source-registered tool
/// call, root-agent-id built-in tool call, and full persistence round-trip including the SDK
/// session id resume signal traversing the wire).
/// </summary>
public sealed class RemoteCopilotSdkSessionTests
{
    private const string StableSdkSessionId = "stable-sdk-session-id";

    private const string RemoteAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "remote-copilot-sdk-agent",
          "model": { "id": "gpt-4o", "provider": "github-copilot" },
          "tools": []
        }
        """;

    [Fact]
    public async Task RemoteCopilotSdkSession_SourceSessionToolInvokedOnSourceInstance()
    {
        var ct = TransportScenarioSupport.TestToken();

        await using var setup = await RemoteSdkSetup.CreateAsync(ct);

        // Scripted single turn: assistant text, source-targeted tool call + result, then idle.
        setup.FakeSession.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "invoking source tool", MessageId = "msg-1" },
        });
        setup.FakeSession.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "call-source", ToolName = "agent_session_add_note" },
        });
        setup.FakeSession.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-source",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "source-note-recorded" },
            },
        });
        setup.FakeSession.EnqueueEvent(new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        await using var chat = await AgentChat.CreateAsync(setup.BuildChatRequest());
        chat.EnqueueUserMessage("please invoke the source tool");
        // user | assistant(text+FC-source) | tool(FR-source) = 3 items (consecutive Assistant-role
        // updates merge into a single history item; the Tool-role result becomes its own item).
        await WaitForHistoryCountAsync(chat.History, 3, "user + assistant(text+FC) + tool(FR)");

        // Verify the source AgentChat observed the SDK-driven source-tool round-trip end-to-end
        // over the wire (i.e., transcript contains FunctionCallContent/FunctionResultContent
        // routed via the remote CopilotSdkChatClient built from the wire-carried AgentDefinition).
        var allContents = chat.History.SelectMany(h => h.Contents).ToArray();
        var call = allContents.OfType<FunctionCallContent>().SingleOrDefault(c => c.CallId == "call-source");
        Assert.NotNull(call);
        Assert.Equal("agent_session_add_note", call!.Name);
        var result = allContents.OfType<FunctionResultContent>().SingleOrDefault(r => r.CallId == "call-source");
        Assert.NotNull(result);
        Assert.Contains(chat.History, h => h.Contents.OfType<TextContent>().Any(t => t.Text == "invoking source tool"));
    }

    [Fact]
    public async Task RemoteCopilotSdkSession_BuiltinPowerShellToolRunsOnRemoteUnderSessionNode()
    {
        var ct = TransportScenarioSupport.TestToken();

        await using var setup = await RemoteSdkSetup.CreateAsync(ct);

        // Scripted turn: a root-AgentId built-in tool (shell) start+complete pair, plus idle.
        setup.FakeSession.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "call-shell", ToolName = "shell" },
        });
        setup.FakeSession.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-shell",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "remote-shell-output" },
            },
        });
        setup.FakeSession.EnqueueEvent(new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        await using var chat = await AgentChat.CreateAsync(setup.BuildChatRequest());
        chat.EnqueueUserMessage("run remote shell");
        await WaitForHistoryCountAsync(chat.History, 3, "user + FC + FR");

        // The root-AgentId built-in tool must appear in history — per #1318 it is pinned at the
        // root/SDK-session sink rather than dropped by CopilotSubAgentRouter. The event round-trips
        // through the wire from the remote CopilotSdkChatClient into the source transcript.
        var allContents = chat.History.SelectMany(h => h.Contents).ToArray();
        var call = allContents.OfType<FunctionCallContent>().SingleOrDefault(c => c.CallId == "call-shell");
        Assert.NotNull(call);
        Assert.Equal("shell", call!.Name);
        var result = allContents.OfType<FunctionResultContent>().SingleOrDefault(r => r.CallId == "call-shell");
        Assert.NotNull(result);

        // #1318 acceptance: root-AgentId built-in tool contents have no ParentToolCallId tag —
        // they belong to the SDK session node, not to a sub-agent branch.
        Assert.Null(Phantom.Workspaces.Llm.CopilotSdkStreamAdapter.GetParentToolCallId(call));
        Assert.Null(Phantom.Workspaces.Llm.CopilotSdkStreamAdapter.GetParentToolCallId(result));
    }

    [Fact]
    public async Task RemoteCopilotSdkSession_HistoryAndPersistenceRoundTripOnSource()
    {
        // Give the reopen path extra headroom vs. TransportScenarioSupport.TestToken's 30s default:
        // test 3 spins up two chats + two remote per-channel CopilotSdkChatClient instances on the
        // shared hub-relay harness, and under parallel test-class load the second turn's channel
        // connect + resume + streaming round-trip has been observed to run beyond 30s while still
        // making progress.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeoutCts.Token;

        // Two independent FakeCopilotSessions eliminate any shared-state race between chat1's
        // remote CopilotSdkChatClient teardown and chat2's new per-channel client on the shared
        // FakeCopilotClient. The SequencedFakeCopilotClient routes the first
        // CreateSession/ResumeSession call to session[0] and the second to session[1].
        await using var setup = await RemoteSdkSetup.CreateAsync(ct, sessionCount: 2);
        var session1 = setup.FakeSessions[0];
        var session2 = setup.FakeSessions[1];

        // First turn: assistant + source tool + builtin tool + idle.
        session1.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "turn-one", MessageId = "msg-1" },
        });
        session1.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "call-source-1", ToolName = "agent_session_add_note" },
        });
        session1.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-source-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "source-ok" },
            },
        });
        session1.EnqueueEvent(new ToolExecutionStartEvent
        {
            AgentId = "",
            Data = new ToolExecutionStartData { ToolCallId = "call-shell-1", ToolName = "shell" },
        });
        session1.EnqueueEvent(new ToolExecutionCompleteEvent
        {
            AgentId = "",
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-shell-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "shell-ok" },
            },
        });
        session1.EnqueueEvent(new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        string sourceAgentSessionId;
        await using (var chat1 = await AgentChat.CreateAsync(setup.BuildChatRequest()))
        {
            chat1.EnqueueUserMessage("first");
            await WaitForHistoryCountAsync(chat1.History, 5, "user + text + source-FC/FR + shell-FC/FR");
            sourceAgentSessionId = chat1.AgentSessionId;
        }

        // Persistence check: the SessionEstablished event must have crossed the wire so the
        // source's IncrementalPersistenceChatHistoryProvider recorded CopilotSdkSessionId.
        var persisted = await setup.SourceStore.RestoreAsync(
            new RestoreRequest { AgentSessionId = sourceAgentSessionId }, ct);
        Assert.NotNull(persisted);
        Assert.Equal(StableSdkSessionId, persisted!.Value.CopilotSdkSessionId);

        var messages = await setup.SourceStore.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = sourceAgentSessionId }, ct);
        Assert.Contains(messages, m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-source-1"));
        Assert.Contains(messages, m => m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call-source-1"));
        Assert.Contains(messages, m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-shell-1"));
        Assert.Contains(messages, m => m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call-shell-1"));

        // Second turn scripting: assistant + idle on session2 (chat2's per-channel remote client
        // will Create-or-Resume this fresh session on the sequenced fake).
        session2.EnqueueEvent(new AssistantMessageDeltaEvent
        {
            AgentId = "",
            Data = new AssistantMessageDeltaData { DeltaContent = "turn-two", MessageId = "msg-2" },
        });
        session2.EnqueueEvent(new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        // Reopen: constructor restores transcript from persistence and, per AgentChat.cs:373,
        // invokes ICopilotSdkSessionSink.SetResumeSessionId(persistedId) — which the source's
        // ChatClientOverTransport forwards over the wire to the remote CopilotSdkChatClient so
        // its next CreateOrResumeSessionAsync resumes rather than creates.
        await using var chat2 = await AgentChat.CreateAsync(setup.BuildChatRequest(sourceAgentSessionId));

        // Full transcript is restored:
        Assert.Contains(chat2.History, h => h.Contents.OfType<TextContent>().Any(t => t.Text.Contains("turn-one")));
        Assert.Contains(chat2.History, h => h.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-source-1"));
        Assert.Contains(chat2.History, h => h.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call-source-1"));
        Assert.Contains(chat2.History, h => h.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-shell-1"));
        Assert.Contains(chat2.History, h => h.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call-shell-1"));

        // Drive a second turn so the remote CopilotSdkChatClient actually calls
        // ResumeSessionAsync on the SDK client with the persisted id. Instead of waiting for the
        // full assistant round-trip (which under parallel test-class load has been observed to be
        // subject to thread-pool contention on the outbound streaming-update frames), we poll the
        // direct observable: session2's OnResumeSession recording the resume id. That is the
        // exact proof of SetResumeSessionId → wire → remote CopilotSdkChatClient.ResumeSessionAsync
        // propagation targeted by #1319 (acceptance criterion #3).
        chat2.EnqueueUserMessage("second");

        await WaitForConditionAsync(
            () => session2.LastResumeSessionId is not null,
            "session2.LastResumeSessionId to be recorded on the reopened chat's remote client",
            timeoutSeconds: 60);

        Assert.Equal(StableSdkSessionId, session2.LastResumeSessionId);
    }

    private sealed class RemoteSdkSetup : IAsyncDisposable
    {
        private readonly HubRelayHarness harness;
        private readonly List<IAsyncDisposable> ownedAsync = [];

        public RemoteSdkSetup(
            HubRelayHarness harness,
            IReadOnlyList<FakeCopilotSession> fakeSessions,
            InMemoryAgentPersistenceStore sourceStore,
            AgentDefinition agentDefinition,
            CancellationToken ct)
        {
            this.harness = harness;
            this.FakeSessions = fakeSessions;
            this.SourceStore = sourceStore;
            this.AgentDefinition = agentDefinition;
            this.CancellationToken = ct;
        }

        // Test 1 and Test 2 only exercise a single per-channel remote CopilotSdkChatClient, so
        // the first (and only) fake session in the sequence is the one they script events on.
        public FakeCopilotSession FakeSession => this.FakeSessions[0];

        public IReadOnlyList<FakeCopilotSession> FakeSessions { get; }

        public InMemoryAgentPersistenceStore SourceStore { get; }

        public AgentDefinition AgentDefinition { get; }

        public CancellationToken CancellationToken { get; }

        public static async Task<RemoteSdkSetup> CreateAsync(CancellationToken ct, int sessionCount = 1)
        {
            var sessions = new List<FakeCopilotSession>(sessionCount);
            for (int i = 0; i < sessionCount; i++)
            {
                sessions.Add(new FakeCopilotSession { SessionId = StableSdkSessionId });
            }

            var fakeClient = new SequencedFakeCopilotClient(sessions);
            var fakeFactory = new FakeCopilotClientFactory(fakeClient);

            var agentDef = AgentDefinitionLoader.LoadAgentFromJson(RemoteAgentDefinitionJson);

            // Remote-side AgentServices carries the FakeCopilotClientFactory so the per-channel
            // CopilotSdkChatClient built by ChatClientTransportListener (#1314) uses the scripted
            // fake SDK (#1316) rather than starting a real copilot.exe.
            var remoteServices = new AgentServices { CopilotClientFactory = fakeFactory };

            var executorRegistry = new TransportRegistry();
            executorRegistry.Register(new ChatClientTransportListener(async (definition, cancel) =>
            {
                var result = await AgentFactory.CreateChatClientAsync(
                    definition,
                    remoteServices,
                    cancellationToken: cancel).ConfigureAwait(false);

                // The remote CopilotSdkChatClient is not wrapped by an AgentChat on the remote
                // side, so no one has called SetSubAgentDependencies. Provide throwing stubs to
                // satisfy the internal ArgumentNullException guards — no sub-agent events are
                // scripted, so the router never calls them.
                if (result.ChatClient is CopilotSdkChatClient copilotClient)
                {
                    copilotClient.SetSubAgentDependencies(new StubRunningAgentChatFactory(), new StubSubAgentTable());
                }

                return result.ChatClient;
            }));

            var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
            var sourceStore = new InMemoryAgentPersistenceStore();

            return new RemoteSdkSetup(harness, sessions, sourceStore, agentDef, ct);
        }

        public InternalCreateAgentChatRequest BuildChatRequest(string? agentSessionId = null)
        {
            var transport = this.harness.ConnectMachineBAsync(this.CancellationToken).GetAwaiter().GetResult();
            this.ownedAsync.Add(transport);

            var chatClientRequest = BuildChatClientRequestPayload(this.AgentDefinition);
            var chatClient = new ChatClientOverTransport(transport, chatClientRequest);
            // ChatClientOverTransport is NOT ISelfInvokingToolChatClient; keeping raw stream
            // routing (no FunctionInvokingChatClient wrapper) is required so FunctionCallContent /
            // FunctionResultContent emitted by the remote CopilotSdkStreamAdapter flow into the
            // source transcript verbatim rather than being auto-invoked locally.
            return new InternalCreateAgentChatRequest
            {
                AgentDefinition = this.AgentDefinition,
                ConfiguredStore = this.SourceStore,
                ClientOverride = chatClient,
                DisplayNameOverride = "remote-copilot-sdk",
                AgentSessionId = agentSessionId,
                OverrideUseProvidedChatClientAsIs = true,
                OwnedResources = [new SyncDisposableAsAsync(chatClient)],
                CancellationToken = this.CancellationToken,
            };
        }

        private static JsonElement BuildChatClientRequestPayload(AgentDefinition definition)
        {
            var payload = new JsonObject
            {
                ["type"] = "chat-client",
                ["agent-definition"] = definition.ToJson(),
            };
            return JsonSerializer.SerializeToElement(payload);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var disposable in this.ownedAsync)
            {
                try
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await this.harness.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class SyncDisposableAsAsync : IAsyncDisposable
        {
            private readonly IDisposable inner;

            public SyncDisposableAsAsync(IDisposable inner)
            {
                this.inner = inner;
            }

            public ValueTask DisposeAsync()
            {
                this.inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        string description,
        int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timeout waiting for {description}.");
            }

            // Backoff between polls to avoid a tight spin under load. This is a polling delay,
            // not a fixed artificial sleep for synchronization — the loop exits as soon as the
            // condition flips.
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static async Task WaitForHistoryCountAsync(
        INotifyCollectionChanged collection,
        int expectedCount,
        string description)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Delay(TimeSpan.FromSeconds(60));

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (TryCheckCount())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnChanged;
        try
        {
            if (TryCheckCount())
            {
                return;
            }

            var completed = await Task.WhenAny(signal.Task, timeout);
            if (completed == timeout)
            {
                int actual;
                try
                {
                    actual = ((System.Collections.ICollection)collection).Count;
                }
                catch (Exception ex)
                {
                    actual = -1;
                    description += $" (Count access threw: {ex.GetType().Name}: {ex.Message})";
                }
                throw new TimeoutException(
                    $"Timeout waiting for {description}. expected={expectedCount}, actual={actual}.");
            }
        }
        finally
        {
            collection.CollectionChanged -= OnChanged;
        }

        bool TryCheckCount()
        {
            try
            {
                return ((System.Collections.ICollection)collection).Count >= expectedCount;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private sealed class StubRunningAgentChatFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotSupportedException("Stub: sub-agent flows are not exercised in this test.");

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Stub: sub-agent flows are not exercised in this test.");

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
            => throw new NotSupportedException("Stub: sub-agent flows are not exercised in this test.");
    }

    private sealed class StubSubAgentTable : ISubAgentTable
    {
        public Task<SubAgent> Add(AgentChat agentChat)
            => throw new NotSupportedException("Stub: sub-agent flows are not exercised in this test.");
    }

    // Vends a distinct FakeCopilotSession per Create/Resume call so each per-channel remote
    // CopilotSdkChatClient built by ChatClientTransportListener (chat1, chat2, ...) operates on
    // its own session state. This eliminates any race between chat1's async DisposeAsync (which
    // clears started/queue on a shared client) and chat2's StartAsync/SendAsync when the two
    // channels' lifetimes overlap during reopen.
    private sealed class SequencedFakeCopilotClient : ICopilotClient
    {
        private readonly IReadOnlyList<FakeCopilotSession> sessions;
        private int nextIndex;
        private bool started;
        private readonly object lockObject = new();

        public SequencedFakeCopilotClient(IReadOnlyList<FakeCopilotSession> sessions)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (this.lockObject)
            {
                this.started = true;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        }

        public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            var session = this.TakeNextSession();
            session.OnCreateSession(config);
            return Task.FromResult<ICopilotSession>(session);
        }

        public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
        {
            var session = this.TakeNextSession();
            session.OnResumeSession(sessionId, config);
            return Task.FromResult<ICopilotSession>(session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private FakeCopilotSession TakeNextSession()
        {
            lock (this.lockObject)
            {
                if (!this.started)
                {
                    throw new InvalidOperationException("Client not started.");
                }

                if (this.nextIndex >= this.sessions.Count)
                {
                    // Reuse the last session for any additional calls (e.g. reconnect after
                    // InvalidateCopilotSession). Tests script exactly as many sessions as there
                    // are remote per-channel CopilotSdkChatClient instances that actually run a
                    // turn, so this branch is defense-in-depth for unexpected extra calls.
                    return this.sessions[^1];
                }

                return this.sessions[this.nextIndex++];
            }
        }
    }
}

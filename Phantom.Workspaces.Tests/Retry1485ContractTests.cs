using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Moq;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

public sealed class Retry1485ContractTests
{
    [Fact]
    public void AcquireAgentChatRequest_RemoteInitProperties_PreserveModeTransportAndCursor()
    {
        var transport = Mock.Of<ITransport>();
        var cursor = new ReplayCursor
        {
            Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            Sequence = 9,
        };
        var hostContext = new CurrentSessionContext
        {
            AgentSessionId = "remote-metadata",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = 2,
            RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
        };

        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("remote-metadata"),
            EntityName = "Entity",
            AgentSessionEntity = RemoteEntity(),
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
            OwningProfileTransport = transport,
            ReplayCursor = cursor,
            AgentServices = new AgentServices { CurrentSessionContext = hostContext },
        };

        Assert.Equal(AgentChatAcquisitionMode.AttachRemote, request.AcquisitionMode);
        Assert.Same(transport, request.OwningProfileTransport);
        Assert.Equal(cursor, request.ReplayCursor);
        Assert.Same(hostContext, request.AgentServices!.CurrentSessionContext);
    }

    [Fact]
    public async Task AttachRemoteAcquisition_TransportBackedIntent_RoundTripsMessageThroughOwningTransport()
    {
        // Uses the sanctioned in-memory transport (InProcessTransport.Create) so the value stored on
        // RemoteRuntimeIntent.OwningProfileTransport is a fully-functional ITransport, not an inert
        // placeholder. The test propagates the client side of the paired transport through the real
        // acquisition path, extracts it back off the forwarded intent, and performs a real
        // message-channel round-trip through it to prove the propagated instance is usable.
        var registry = new TransportRegistry();
        var listener = new EchoTransportListener();
        registry.Register(listener);
        var (server, client) = InProcessTransport.Create(registry);

        try
        {
            var request = new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("transport-backed"),
                EntityName = "Entity",
                AgentSessionEntity = RemoteEntity(),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                OwningProfileTransport = client,
            };

            var propagated = Assert.IsAssignableFrom<ITransport>(request.OwningProfileTransport);
            Assert.Same(client, propagated);

            var transportRequest = JsonDocument.Parse("{\"op\":\"attach\"}").RootElement;
            var channel = await propagated.ConnectToMessageChannelAsync(transportRequest, TestContext.Current.CancellationToken);
            try
            {
                await listener.ChannelOpened.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                var payload = JsonDocument.Parse("{\"probe\":true}").RootElement;
                await channel.Writer.WriteAsync(payload, TestContext.Current.CancellationToken);
                var response = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
                Assert.Equal(payload.GetRawText(), response.GetRawText());
            }
            finally
            {
                await channel.DisposeAsync();
            }
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private sealed class EchoTransportListener : ITransportListener
    {
        private readonly TaskCompletionSource channelOpened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ChannelOpened => this.channelOpened.Task;

        public Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in channel.Reader.ReadAllAsync(ct))
                    {
                        await channel.Writer.WriteAsync(message, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, ct);

            this.channelOpened.TrySetResult();
            return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(
            JsonElement request,
            Stream stream,
            CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AcquireAgentChatRequest_Defaults_SelectLocalModeWithoutTransportOrCursor()
    {
        var factory = new TestRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);

        await using var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("local-default"),
            EntityName = "Entity",
        }, TestContext.Current.CancellationToken);

        Assert.IsType<AgentChat>(lease.AgentChat);
        Assert.Null(factory.LastServices?.RemoteRuntimeIntent);
        Assert.False(Assert.Single(table.RunningSessions).IsRemote);
    }

    [Fact]
    public async Task AcquireAgentChatRequest_InvalidModeCombination_AcquireRejectsRequest()
    {
        var factory = new TestRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var invalidRequests = new[]
        {
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-local-transport"),
                AcquisitionMode = AgentChatAcquisitionMode.Local,
                AgentSessionEntity = RemoteEntity(),
                OwningProfileTransport = Mock.Of<ITransport>(),
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-attach-missing-transport"),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                AgentSessionEntity = RemoteEntity(),
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-start-missing-transport"),
                AcquisitionMode = AgentChatAcquisitionMode.StartOrAttachRemote,
                AgentSessionEntity = RemoteEntity(),
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-negative-generation"),
                AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
                AgentSessionEntity = JsonDocument.Parse("""{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":-1}""").RootElement.Clone(),
                OwningProfileTransport = Mock.Of<ITransport>(),
            },
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("invalid-unknown-mode"),
                AcquisitionMode = (AgentChatAcquisitionMode)999,
            },
        };

        foreach (var request in invalidRequests)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => table.AcquireAsync(request, TestContext.Current.CancellationToken));
        }

        Assert.Empty(factory.RunningSessions);
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_RetentionMetadataChange_RaisesAuthoritativeUpdate()
    {
        var factory = new TestRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory, new FakeRuntimeContextFactory());
        var sessionId = new AgentSessionId("retention-metadata");
        var changes = new List<string?>();

        await using var localLease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = sessionId,
            EntityName = "Entity",
        }, TestContext.Current.CancellationToken);
        var entry = Assert.Single(table.RunningSessions);
        entry.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        await table.SetContinueInBackgroundAsync(sessionId, true, TestContext.Current.CancellationToken);
        Assert.True(entry.ContinueInBackground);
        Assert.Contains(nameof(RunningAgentChatWithEntityInfo.ContinueInBackground), changes);
    }

    [Fact]
    public void CurrentSessionContext_ValidOwnerGenerationEpoch_PreservesOwningHostIdentity()
    {
        var epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
        var ctx = new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = 3,
            RuntimeEpoch = epoch,
        };
        Assert.Equal("s-1", ctx.AgentSessionId);
        Assert.Equal("host-A", ctx.OwningProfileEntityId);
        Assert.Equal(3, ctx.OwnershipGeneration);
        Assert.Equal(epoch, ctx.RuntimeEpoch);
    }

    [Fact]
    public void CurrentSessionContext_BlankOwner_RejectsInitialization()
        => Assert.Throws<ArgumentException>(() => new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            OwningProfileEntityId = "   ",
            OwnershipGeneration = 0,
        });

    [Fact]
    public void CurrentSessionContext_NegativeGeneration_RejectsInitialization()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = -1,
        });

    [Fact]
    public void CurrentSessionContext_RuntimeEpochAbsent_PreservesNull()
    {
        var context = new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = 0,
        };

        Assert.Null(context.RuntimeEpoch);
    }

    [Fact]
    public void CurrentSessionContext_OwnerAndGeneration_AreMarkedRequired()
    {
        var required = typeof(CurrentSessionContext).GetProperties()
            .Where(property => property.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(System.Runtime.CompilerServices.RequiredMemberAttribute)))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(CurrentSessionContext.OwningProfileEntityId), required);
        Assert.Contains(nameof(CurrentSessionContext.OwnershipGeneration), required);
        Assert.DoesNotContain(nameof(CurrentSessionContext.RuntimeEpoch), required);
        Assert.Equal(typeof(RuntimeEpoch?), typeof(CurrentSessionContext)
            .GetProperty(nameof(CurrentSessionContext.RuntimeEpoch))!.PropertyType);
    }

    [Fact]
    public void CurrentSessionContext_AttachmentPeer_DoesNotReplaceHostIdentity()
    {
        var hostContext = new CurrentSessionContext
        {
            AgentSessionId = "attach-host",
            OwningProfileEntityId = "host-A",
            OwnershipGeneration = 2,
            RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
        };

        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("attach-host"),
            EntityName = "Entity",
            AgentSessionEntity = RemoteEntity(),
            AcquisitionMode = AgentChatAcquisitionMode.AttachRemote,
            OwningProfileTransport = Mock.Of<ITransport>(),
            AgentServices = new AgentServices { CurrentSessionContext = hostContext },
        };

        var forwarded = Assert.IsType<CurrentSessionContext>(request.AgentServices!.CurrentSessionContext);
        Assert.Equal("host-A", forwarded.OwningProfileEntityId);
        Assert.Equal(2, forwarded.OwnershipGeneration);
        Assert.Equal(hostContext.RuntimeEpoch, forwarded.RuntimeEpoch);
    }

    [Fact]
    public void AgentServices_AgentExecutionTrustContextSetter_WithExpressionPreservesOtherServices()
    {
        var s = new AgentServices { LogChat = true, LogHttpRequests = true };
        var withTrust = s with { ExecutionTrustContext = new object() };
        Assert.True(withTrust.LogChat);
        Assert.True(withTrust.LogHttpRequests);
        Assert.NotNull(withTrust.ExecutionTrustContext);
    }

    [Fact]
    public void AgentServices_RemoteRuntimeIntentSetter_WithExpressionPreservesOtherServices()
    {
        var s = new AgentServices { LogChat = true, LogHttpRequests = true };
        var updated = s with { RemoteRuntimeIntent = new object() };
        Assert.True(updated.LogChat);
        Assert.True(updated.LogHttpRequests);
        Assert.NotNull(updated.RemoteRuntimeIntent);
    }

    [Fact]
    public void AgentServices_RuntimeContextSeams_WithExpressionPreserveOtherServices()
    {
        var original = new AgentServices { LogChat = true };
        var trust = new object();
        var remote = new object();

        var updated = original with
        {
            AgentExecutionTrustContext = trust,
            RemoteAgentSessionRuntimeIntent = remote,
        };

        Assert.True(updated.LogChat);
        Assert.Same(trust, updated.AgentExecutionTrustContext);
        Assert.Same(remote, updated.RemoteAgentSessionRuntimeIntent);
        Assert.Null(updated.GetService(trust.GetType()));
    }

    [Fact]
    public void AgentServices_GetService_NewObjectTypedSeams_DoesNotExposeConcreteTypes()
    {
        var t = typeof(AgentServices);
        foreach (var name in new[]
        {
            nameof(AgentServices.CurrentSessionContext),
            nameof(AgentServices.SecretProvider),
            nameof(AgentServices.SecretPlaceholderResolver),
            nameof(AgentServices.McpOAuthOptions),
            nameof(AgentServices.CopilotClientFactory),
            nameof(AgentServices.CurrentAgentChatRef),
            nameof(AgentServices.ExecutorBindings),
            nameof(AgentServices.ExecutorTransportFactoryRegistry),
            nameof(AgentServices.TrustProfileResolver),
            nameof(AgentServices.TrustProfilePolicyCompiler),
            nameof(AgentServices.ProcessExecutor),
            nameof(AgentServices.ExecutionTrustContext),
            nameof(AgentServices.SlashCommandRegistry),
            nameof(AgentServices.RemoteRuntimeIntent),
        })
        {
            var prop = t.GetProperty(name);
            Assert.NotNull(prop);
            Assert.Equal(typeof(object), prop!.PropertyType);
        }

        var services = new AgentServices { RemoteRuntimeIntent = new object() };
        Assert.Null(services.GetService(typeof(object)));
    }

    private static JsonElement RemoteEntity()
        => JsonDocument.Parse("""{"host-profile-entity-id":"11111111-1111-1111-1111-111111111111","ownership-generation":2}""").RootElement.Clone();

    private sealed class TestRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly Dictionary<AgentSessionId, (int RefCount, RunningAgentChat Entry, Task<AgentChat> ChatTask)> sessions = new();

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];
        public AgentDefinition? LastDefinition { get; private set; }
        public AgentServices? LastServices { get; private set; }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => this.GetOrCreateAsync(sessionId, registerAsRunningAgent: registerAsRunningAgent, ct: ct);

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => this.GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, registerAsRunningAgent: true, ct: ct);

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
        {
            this.LastDefinition = definition;
            this.LastServices = services;
            bool isNew = false;
            RunningAgentChat? entryToAdd = null;
            Task<AgentChat> chatTask;

            lock (this.sessions)
            {
                if (this.sessions.TryGetValue(sessionId, out var existing))
                {
                    this.sessions[sessionId] = (existing.RefCount + 1, existing.Entry, existing.ChatTask);
                    chatTask = existing.ChatTask;
                }
                else
                {
                    isNew = true;
                    entryToAdd = new RunningAgentChat(sessionId, this);
                    chatTask = AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
                    {
                        AgentDefinition = definition ?? TestDefinition(),
                        AgentServices = services,
                    });
                    this.sessions[sessionId] = (1, entryToAdd, chatTask);
                }
            }

            var chat = await chatTask;
            if (isNew && registerAsRunningAgent && entryToAdd is not null)
            {
                this.RunningSessions.Add(entryToAdd);
            }

            return new RunningAgentChatLease(
                sessionId,
                chat,
                onDispose: () => this.ReleaseAsync(sessionId),
                localAgentChat: chat);
        }

        private async ValueTask ReleaseAsync(AgentSessionId sessionId)
        {
            Task<AgentChat>? chatTask = null;
            RunningAgentChat? entry = null;

            lock (this.sessions)
            {
                if (!this.sessions.TryGetValue(sessionId, out var existing))
                {
                    return;
                }

                if (existing.RefCount == 1)
                {
                    this.sessions.Remove(sessionId);
                    chatTask = existing.ChatTask;
                    entry = existing.Entry;
                }
                else
                {
                    this.sessions[sessionId] = (existing.RefCount - 1, existing.Entry, existing.ChatTask);
                }
            }

            if (entry is not null)
            {
                this.RunningSessions.Remove(entry);
            }

            if (chatTask is not null)
            {
                await (await chatTask).DisposeAsync();
            }
        }
    }

    private sealed class FakeRuntimeContextFactory : IAgentSessionRuntimeContextFactory
    {
        public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
            => new()
            {
                Intent = new PersistedAgentSessionRuntimeIntent
                {
                    AgentSessionId = "fake-session",
                    OwningProfileEntityId = "11111111-1111-1111-1111-111111111111",
                    OwnershipGeneration = 0,
                    ExecutorBindings = new ExecutorBindings
                    {
                        SessionExecutor = JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone(),
                    },
                },
            };
    }

    private static AgentDefinition TestDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            { "kind": "prompt", "name": "retry-1485-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [] }
            """);
}

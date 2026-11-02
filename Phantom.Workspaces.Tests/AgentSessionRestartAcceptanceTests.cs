using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Fact]
    public async Task RestartSession_FailedStartup_StopsUnpublishedRenewalAndRecoversAfterItsLeaseExpires()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        environment.FailStartup = true;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        var stopped = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Stopped, stopped.State);
        var expiry = environment.Persisted.Data!.Value.GetProperty("runtime-lease-expiry").GetString();
        environment.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(expiry, environment.Persisted.Data!.Value.GetProperty("runtime-lease-expiry").GetString());
        environment.FailStartup = false;

        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.NotEqual(stopped.ReplacementRuntime, result.ReplacementRuntime);
        Assert.Single(environment.Table.RunningSessions);
        Assert.NotSame(viewer.AgentChat,
            (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat);
    }

    [Fact]
    public async Task RestartSession_InitialCommitAcknowledgementLost_ReconcilesDurableCommitAndReplacesExactlyOnce()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var lost = false;
        environment.OnUpdated = update =>
        {
            if (!lost && update.UpdateMetadata?.Comment?.Text == "Record agent session restart operation")
            {
                lost = true;
                throw new IOException("Committed journal acknowledgement lost.");
            }
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        var replacement = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var repeated = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.True(lost);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.Equal(result, repeated);
        Assert.Same(replacement, await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken));
        Assert.True(Assert.IsType<AgentChat>(viewer.AgentChat).IsTerminal);
        Assert.Single(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_OverlappedTakeover_RejectsCapturedGenerationAndNeverStopsNewOwner()
    {
        await using var environment = new RestartEnvironment();
        await using var original = await environment.AcquireAsync();
        var old = Assert.IsType<AgentChat>(original.AgentChat);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            stopping.TrySetResult();
            return new ValueTask(release.Task);
        });
        old.RegisterOwnedResource(resource.Object);
        var takeover = environment.Host.TakeOverAsync(RestartPeer(), new AgentSessionTakeoverRequest
        {
            AgentSessionId = "session",
            ExpectedOwningProfileEntityId = Owner,
            ExpectedOwnershipGeneration = 3,
            NewOwningProfileEntityId = Owner,
            CorrelationId = Guid.NewGuid(),
        }, TestContext.Current.CancellationToken);
        await stopping.Task.WaitAsync(TestContext.Current.CancellationToken);
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = original.RestartIdentity!,
        };
        var restart = original.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        var queued = !restart.IsCompleted;
        release.TrySetResult();
        await takeover;
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() => restart);
        Assert.True(queued);

        await using var newOwner = await environment.AcquireAsync();
        var current = Assert.IsType<AgentChat>(newOwner.AgentChat);
        Assert.NotSame(old, current);
        Assert.Equal(4, newOwner.RestartIdentityProvider!()!.OwnershipGeneration);
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() =>
            original.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
        Assert.False(current.IsTerminal);
        Assert.Single(environment.Table.RunningSessions);
        Assert.Same(current, (await environment.Registry.TryGetAsync("session", 4, TestContext.Current.CancellationToken))!.Chat);
        resource.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestartSession_UnpublishedReplacementCleanup_FencesFailureAndRetriesOnlyConfirmedShutdown(
        bool failCleanup, bool failReconciliation)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        AgentChat? unpublished = null;
        var cleanup = new Mock<IAsyncDisposable>();
        cleanup.Setup(value => value.DisposeAsync()).Returns(() => failCleanup
            ? ValueTask.FromException(new IOException("Unpublished runtime resource failed to stop."))
            : ValueTask.CompletedTask);
        environment.BeforePublication = replacement =>
        {
            environment.BeforePublication = null;
            unpublished = Assert.IsType<AgentChat>(replacement.Chat);
            unpublished.RegisterOwnedResource(cleanup.Object);
            environment.FailNextQuery = failReconciliation;
            throw new IOException("Registry unavailable before publication.");
        };

        var error = await Record.ExceptionAsync(() => viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
        if (failCleanup || failReconciliation)
            Assert.NotNull(error);
        else
            Assert.Null(error);
        Assert.NotNull(unpublished);
        Assert.Equal(!failCleanup, unpublished.IsTerminal);
        Assert.True(unpublished.InputQueueManager.IsRuntimeHandoff);
        Assert.Empty(environment.Table.RunningSessions);
        cleanup.Verify(value => value.DisposeAsync(), Times.Once);
        var status = await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(failCleanup || failReconciliation
            ? AgentSessionOwnerRestartState.Blocked : AgentSessionOwnerRestartState.Stopped, status!.State);

        var retry = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(failCleanup || failReconciliation
            ? AgentSessionOwnerRestartState.Blocked : AgentSessionOwnerRestartState.Succeeded, retry.State);
        if (failCleanup || failReconciliation)
            Assert.Empty(environment.Table.RunningSessions);
        else
            Assert.NotSame(unpublished,
                (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat);
        cleanup.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("committed-generation")]
    [InlineData("startup-generation")]
    [InlineData("startup-epoch")]
    [InlineData("startup-expiry")]
    public async Task RestartSession_AuthorityChangesAtDestructiveBoundaries_StopsOnlyItsExactRuntime(string boundary)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var old = Assert.IsType<AgentChat>(viewer.AgentChat);
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        var externalEpoch = Guid.NewGuid().ToString("D");
        var changes = boundary.EndsWith("generation", StringComparison.Ordinal)
            ? new Dictionary<string, JsonElement>
            {
                ["ownership-generation"] = JsonSerializer.SerializeToElement(4),
                ["runtime-epoch"] = JsonSerializer.SerializeToElement(externalEpoch),
            }
            : boundary.EndsWith("epoch", StringComparison.Ordinal)
            ? new Dictionary<string, JsonElement>
            {
                ["runtime-epoch"] = JsonSerializer.SerializeToElement(externalEpoch),
            }
            : new Dictionary<string, JsonElement>
            {
                ["runtime-lease-expiry"] = JsonSerializer.SerializeToElement(environment.Time.GetUtcNow().ToString("O")),
            };
        var changed = false;
        environment.OnUpdated = update =>
        {
            var comment = update.UpdateMetadata?.Comment?.Text;
            if (changed || (boundary == "committed-generation"
                ? comment != "Record agent session restart operation"
                : comment != "Fence replacement agent session runtime epoch"))
                return;
            changed = true;
            environment.ChangeAuthority(changes);
        };

        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(AgentSessionOwnerRestartState.TargetChanged, result.State);
        Assert.True(old.IsTerminal);
        Assert.Empty(environment.Table.RunningSessions);
        foreach (var change in changes)
            Assert.Equal(change.Value.GetRawText(), environment.Persisted.Data!.Value.GetProperty(change.Key).GetRawText());
        var retry = await Record.ExceptionAsync(() => viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
        if (boundary.EndsWith("generation", StringComparison.Ordinal))
            Assert.IsType<AgentSessionUnavailableException>(retry);
        Assert.Empty(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_OpenAdmissionBeforeRestartGate_IsIncludedInExactViewerCheckpoint()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var runtime = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        using var transition = await runtime.EnterRestartTransitionAsync(TestContext.Current.CancellationToken);
        await using var channel = new RestartBufferedChannel();
        var open = environment.Host.OpenAsync(new OpenAgentSessionHostRequest
        {
            Peer = RestartPeer(),
            OpenRequest = new AgentSessionOpenRequest
            {
                ProtocolVersion = 1,
                Capabilities = [],
                AgentSessionId = "session",
                ExpectedOwningProfileEntityId = Owner,
                ExpectedOwnershipGeneration = 3,
                OpenIntent = AgentSessionOpenIntent.Attach,
                AttachmentToken = "admission-token",
            },
            Channel = channel,
        }, TestContext.Current.CancellationToken);
        var restart = viewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        }, TestContext.Current.CancellationToken);
        Assert.False(open.IsCompleted);
        Assert.False(restart.IsCompleted);
        transition.Dispose();
        RemoteAgentAttachmentLease? attachment = null;
        try { attachment = await open; }
        catch (AgentSessionUnavailableException) { }
        var result = await restart;
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.Contains("admission-token", environment.Persisted.Data!.Value.GetProperty("restart-operations").GetRawText());
        var replacement = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        Assert.True(Assert.IsType<AgentChat>(replacement.Chat).InputQueueManager.IsRuntimeHandoff);
        environment.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(Assert.IsType<AgentChat>(replacement.Chat).InputQueueManager.IsRuntimeHandoff);
        if (attachment is not null)
            await attachment.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_OverlapOldEpochTerminateOrFinalViewerRelease_DoesNotStopReplacement(bool releaseViewer)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var oldChat = Assert.IsType<AgentChat>(viewer.AgentChat);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            stopping.TrySetResult();
            return new ValueTask(release.Task);
        });
        oldChat.RegisterOwnedResource(resource.Object);
        var identity = viewer.RestartIdentity!;
        var restart = viewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = identity,
        }, TestContext.Current.CancellationToken);
        await stopping.Task.WaitAsync(TestContext.Current.CancellationToken);
        var competing = releaseViewer
            ? viewer.DisposeAsync().AsTask()
            : environment.Host.TerminateLocalRuntimeAsync(RestartPeer(), identity, TestContext.Current.CancellationToken);
        var queued = !competing.IsCompleted;
        release.TrySetResult();
        var result = await restart;
        await competing;

        Assert.True(queued);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        var replacement = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        Assert.False(Assert.IsType<AgentChat>(replacement.Chat).IsTerminal);
        Assert.NotSame(oldChat, replacement.Chat);
        Assert.Single(environment.Table.RunningSessions);
        resource.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RestartSession_PartialLocalReattachmentFailure_RetryCompletesSamePublishedRuntime()
    {
        await using var environment = new RestartEnvironment();
        await using var first = await environment.AcquireAsync();
        await using var second = await environment.AcquireAsync();
        RunningAgentChatLease? completed = null;
        first.ReattachAsync = async replacement =>
        {
            completed = replacement;
            await first.DisposeAsync();
        };
        var failOnce = true;
        RunningAgentChatLease? repaired = null;
        second.ReattachAsync = replacement =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new IOException("Viewer composition unavailable once.");
            }
            repaired = replacement;
            return Task.CompletedTask;
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = first.RestartIdentity!,
        };
        var stopped = await first.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        var published = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentSessionOwnerRestartState.Stopped, stopped.State);

        var result = await second.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.Same(published, await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken));
        Assert.NotNull(completed);
        Assert.NotNull(repaired);
        Assert.Same(completed.AgentChat, repaired.AgentChat);
        await completed.DisposeAsync();
        await repaired.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_ViewerReadyBarrier_RequiresExactConnectedViewerOrDisconnectedGrace(bool disconnect)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var runtime = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        await using var channel = new RestartBufferedChannel();
        await using var attachment = runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "exact-token",
            Channel = channel,
        });
        var resumed = 0;
        var chat = Assert.IsType<AgentChat>(runtime.Chat);
        chat.BeginRuntimeHandoff();
        runtime.ResumeAfterRestartViewersReady(["exact-token"], () =>
        {
            chat.InputQueueManager.ResumeAfterRuntimeHandoff();
            resumed++;
        });
        runtime.AcknowledgeRestartViewer("other-viewer");
        Assert.Equal(0, resumed);
        Assert.True(chat.InputQueueManager.IsRuntimeHandoff);
        if (disconnect)
            await attachment.MarkTransportLostAsync();

        environment.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(disconnect ? 1 : 0, resumed);
        Assert.Equal(!disconnect, chat.InputQueueManager.IsRuntimeHandoff);
        runtime.AcknowledgeRestartViewer("exact-token");
        Assert.Equal(1, resumed);
        Assert.False(chat.InputQueueManager.IsRuntimeHandoff);
        runtime.AcknowledgeRestartViewer("exact-token");
        Assert.Equal(1, resumed);
    }

    private static TransportPeerIdentity RestartPeer() => new()
    {
        AuthenticationScheme = "local-workspace-session",
        StablePeerId = "local",
        UserEntityId = "55555555-5555-5555-5555-555555555555",
        UserComputerProfileEntityId = Owner,
    };

    [Fact]
    public async Task RestartSession_DroppedWireResult_StatusAndSameCommandRetryReturnExactlyOneReplacement()
    {
        await using var environment = new RestartEnvironment(dropRestartResult: true);
        await using var viewer = await environment.AcquireAsync();
        await using var remote = await environment.AttachRemoteAsync();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = remote.RestartClient.RestartIdentity!,
        };
        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            remote.RestartClient.RestartAsync(request, TestContext.Current.CancellationToken));
        var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;

        var status = await remote.RestartClient.GetRestartStatusAsync(request, TestContext.Current.CancellationToken);
        var retry = await remote.RestartClient.RestartAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, status!.State);
        Assert.Equal(status, retry);
        Assert.Same(current, await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken));
        Assert.Single(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_BoundChildAcquiredWhileUnavailable_RecoversExistingLocalAndRemoteCapability()
    {
        await using var environment = new RestartEnvironment();
        await using var parentEnvironment = new RestartEnvironment();
        await using var initial = await environment.AcquireAsync();
        await using var parentViewer = await parentEnvironment.AcquireAsync();
        var child = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var parent = (await parentEnvironment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        Assert.True(parent.TryKeepChildAlive(child));
        await using var boundViewer = await environment.AcquireAsync();
        await using var remote = await environment.AttachRemoteAsync();
        Assert.Null(boundViewer.RestartIdentityProvider!());
        Assert.NotNull(boundViewer.RestartRuntimeAsync);
        Assert.Null(remote.RestartClient.RestartIdentity);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RuntimeStateChanged += (_, _) =>
        {
            if (remote.RestartClient.RestartIdentity is not null)
                changed.TrySetResult();
        };

        await parent.DisposeAsync();
        await changed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(boundViewer.RestartIdentityProvider!());
        Assert.Null(remote.RestartClient.RestartDisabledReason);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await boundViewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
            {
                CommandId = Guid.NewGuid(),
                ExpectedRuntime = boundViewer.RestartIdentityProvider()!,
            }, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task RestartSession_BackpressuredViewer_IsFencedBeforeReplacementAndOwnerGateRemainsUsable()
    {
        await using var environment = new RestartEnvironment();
        await using var local = await environment.AcquireAsync();
        var original = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        await using var channel = new RestartBlockedChannel();
        await using var attachment = original.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "blocked-viewer",
            Channel = channel,
        });
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = local.RestartIdentity!,
        };
        var restart = local.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        await channel.Blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(environment.Table.RunningSessions);
        Assert.False(restart.IsCompleted);

        environment.Time.Advance(TimeSpan.FromSeconds(5));
        var result = await restart.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.True(original.HasTerminated);
        Assert.True(channel.WriteCancelled.Task.IsCompleted);
        Assert.Equal(result, await local.RestartStatusAsync!(request, TestContext.Current.CancellationToken));
        Assert.Equal(result, await local.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
        var replacement = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        Assert.NotSame(original.Chat, replacement.Chat);
        environment.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(Assert.IsType<AgentChat>(replacement.Chat).InputQueueManager.IsRuntimeHandoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_NonemptyPersistedTranscriptAndLiveParameters_SurviveReplacementAndStoppedRecovery(
        bool recoverStoppedHost)
    {
        var originalHost = new RestartEnvironment();
        var definition = BsonDocument.Parse(originalHost.Persisted.Data!.Value.GetProperty("definition").GetRawText());
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "prior completed question"),
            new(ChatRole.Assistant, [new FunctionCallContent("prior-call", "prior-tool", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("prior-call", "prior-result")]),
            new(ChatRole.Assistant, "prior completed answer"),
        };
        await originalHost.Store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent { AgentSessionId = "session", AgentDefinitionJson = definition },
            NewMessages = messages,
        }, TestContext.Current.CancellationToken);
        await using var first = await originalHost.AcquireAsync();
        await using var second = await originalHost.AcquireAsync();
        var original = Assert.IsType<AgentChat>(first.AgentChat);
        await original.HistoryPopulated;
        var expectedHistory = Transcript(original);
        Assert.Contains("prior completed answer", expectedHistory);
        Assert.Contains("prior-result", expectedHistory);
        original.UpdateParameterValues(new Dictionary<string, string>
        {
            ["working-directory"] = @"C:\changed-after-start",
            ["restart-parameter"] = "live-value",
        });
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = first.RestartIdentity!,
        };
        if (recoverStoppedHost)
        {
            originalHost.FailStartup = true;
            Assert.Equal(AgentSessionOwnerRestartState.Stopped,
                (await first.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
            await originalHost.DisposeAsync();
            await using var recovered = new RestartEnvironment(originalHost.Store, originalHost.Persisted);
            recovered.Time.Advance(TimeSpan.FromSeconds(31));
            await using var local = await recovered.AcquireAsync();
            await using var remote = await recovered.AttachRemoteAsync();
            Assert.Equal(expectedHistory, Transcript(local.AgentChat));
            Assert.Equal(expectedHistory, Transcript(remote));
            Assert.Equal("live-value", Assert.IsType<AgentChat>(local.AgentChat)
                .BeginRuntimeHandoff().ParameterValues["restart-parameter"]);
            return;
        }
        await using (originalHost)
        {
            await using var remote = await originalHost.AttachRemoteAsync();
            var rebound = new List<RunningAgentChatLease>();
            first.ReattachAsync = replacement => { rebound.Add(replacement); return Task.CompletedTask; };
            second.ReattachAsync = replacement => { rebound.Add(replacement); return Task.CompletedTask; };
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.RestartClient.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
            remote.InformationChanged += (_, _) =>
            {
                if (remote.RestartClient.RestartIdentity?.RuntimeEpoch != request.ExpectedRuntime.RuntimeEpoch)
                    applied.TrySetResult();
            };
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
                (await first.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
            await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);
            await remote.ReconnectNowAsync(TestContext.Current.CancellationToken);
            await applied.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, rebound.Count);
            Assert.All(rebound, lease =>
            {
                Assert.Equal("session", lease.SessionId.Value);
                Assert.Equal(expectedHistory, Transcript(lease.AgentChat));
            });
            Assert.Equal(expectedHistory, Transcript(remote));
            var checkpoint = Assert.IsType<AgentChat>(rebound[0].AgentChat).BeginRuntimeHandoff();
            Assert.Equal(@"C:\changed-after-start", checkpoint.ParameterValues["working-directory"]);
            foreach (var lease in rebound)
                await lease.DisposeAsync();
        }
    }

    private static string Transcript(IAgentChat chat) => JsonSerializer.Serialize(
        chat.History.Where(item => item.Role != AgentChatHistoryItem.DiagnosticChatRole)
            .Select(item => new { item.Role, item.Contents }),
        AIJsonUtilities.DefaultOptions);

    private sealed class RestartBlockedChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChannelReader<JsonElement> Reader => this.incoming.Reader;
        public ChannelWriter<JsonElement> Writer => this.writer ??= new BlockedWriter(this);
        private BlockedWriter? writer;
        public ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed class BlockedWriter(RestartBlockedChannel owner) : ChannelWriter<JsonElement>
        {
            public override bool TryComplete(Exception? error = null) => true;
            public override bool TryWrite(JsonElement item) => false;
            public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            {
                owner.Blocked.TrySetResult();
                try
                {
                    await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
                        .WaitAsync(cancellationToken);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    owner.WriteCancelled.TrySetResult();
                    throw;
                }
            }
        }
    }

    private sealed class RestartBufferedChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();
        public ChannelReader<JsonElement> Reader => this.messages.Reader;
        public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
        public ValueTask DisposeAsync()
        {
            this.messages.Writer.TryComplete();
            this.outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestartPublicationRegistry(
        IRemoteAgentSessionRuntimeRegistry inner,
        Action<RemoteAgentSessionLease> beforePublication) : IRemoteAgentSessionRuntimeRegistry
    {
        public ValueTask<RemoteAgentSessionLease?> TryGetAsync(string sessionId, long ownershipGeneration, CancellationToken ct = default)
            => inner.TryGetAsync(sessionId, ownershipGeneration, ct);
        public ValueTask<RemoteAgentSessionLease> GetOrStartAsync(
            PersistedAgentSessionRuntimeIntent intent, Func<CancellationToken, Task<RemoteAgentSessionLease>> startAsync,
            CancellationToken ct = default)
            => inner.GetOrStartAsync(intent, startAsync, ct);
        public ValueTask<bool> TryTerminateAsync(TerminateAgentSessionRuntimeRequest request, CancellationToken ct = default)
            => inner.TryTerminateAsync(request, ct);
        public ValueTask SetContinueInBackgroundAsync(UpdateAgentSessionRuntimeRetentionRequest request, CancellationToken ct = default)
            => inner.SetContinueInBackgroundAsync(request, ct);
        public ValueTask ReplaceForRestartAsync(
            RemoteAgentSessionLease? expected, RemoteAgentSessionLease replacement, CancellationToken cancellationToken)
        {
            beforePublication(replacement);
            return inner.ReplaceForRestartAsync(expected, replacement, cancellationToken);
        }
    }
}

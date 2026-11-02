using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_EffectiveToolsAndParameters_AreFencedAndRestoredFromDurableCheckpoint(
        bool recoverCheckpoint)
    {
        var sessionId = new AgentSessionId("restart-effective-configuration");
        var store = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            ChatClientOverride = new DeterministicTestChatClient(),
            ToolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
                kind: "restart-toolset",
                createToolsetAsync: (_, _) => Task.FromResult<Microsoft.Agents.AI.AIContextProvider?>(
                    ToolsetFactory.CreateFixedToolset(AIFunctionFactory.Create(() => "result", "restart-tool")))),
        };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            EchoAgentDefinitionJson.Replace("\"tools\": []",
                "\"tools\": [{ \"kind\": \"restart-toolset\" }]"));
        await using var original = await factory.GetOrCreateAsync(sessionId, definition);
        await original.LocalAgentChat.Initialization;
        var tool = Assert.Single(original.AgentChat.GetToolSnapshot());
        await original.AgentChat.SetToolEnabledAsync(tool.Id, false, CancellationToken.None);
        original.LocalAgentChat.UpdateParameterValues(new Dictionary<string, string>
        {
            ["working-directory"] = @"C:\effective-not-definition",
            ["other-live-option"] = "changed",
        });
        AgentRuntimeQueueCheckpoint? durable = null;
        await using var replacement = await factory.RestartExactAsync(
            sessionId,
            original.AgentChat,
            null,
            async (checkpoint, _) =>
            {
                durable = System.Text.Json.JsonSerializer.Deserialize<AgentRuntimeQueueCheckpoint>(
                    System.Text.Json.JsonSerializer.Serialize(checkpoint, AIJsonUtilities.DefaultOptions),
                    AIJsonUtilities.DefaultOptions);
                Assert.Throws<InvalidOperationException>(() =>
                    original.LocalAgentChat.UpdateParameterValues(new Dictionary<string, string> { ["changed"] = "late" }));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    original.AgentChat.SetToolEnabledAsync(tool.Id, true, CancellationToken.None));
            },
            CancellationToken.None);
        await replacement.LocalAgentChat.Initialization;
        Assert.False(Assert.Single(replacement.AgentChat.GetToolSnapshot()).IsEnabled);
        await replacement.AgentChat.SetToolEnabledAsync(tool.Id, true, CancellationToken.None);
        var roots = (System.Collections.IList)typeof(AgentChat)
            .GetField("toolRoots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(replacement.LocalAgentChat)!;
        var rediscovered = Array.CreateInstance(roots[0]!.GetType(), roots.Count);
        roots.CopyTo(rediscovered, 0);
        await (Task)typeof(AgentChat)
            .GetMethod("PublishToolRootsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(replacement.LocalAgentChat, [rediscovered, CancellationToken.None])!;
        Assert.True(Assert.Single(replacement.AgentChat.GetToolSnapshot()).IsEnabled);
        Assert.Equal(@"C:\effective-not-definition", replacement.LocalAgentChat.BeginRuntimeHandoff()
            .ParameterValues["working-directory"]);
        if (recoverCheckpoint)
        {
            await using var recovered = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = null,
                AgentSessionId = sessionId.Value,
                AgentServices = services,
                ConfiguredStore = store,
                ClientOverride = services.ChatClientOverride,
                RuntimeQueueCheckpoint = durable,
                ForegroundScheduler = TaskScheduler.Default,
            });
            await recovered.Initialization;
            Assert.False(Assert.Single(recovered.GetToolSnapshot()).IsEnabled);
            Assert.Equal("changed", recovered.BeginRuntimeHandoff().ParameterValues["other-live-option"]);
        }
    }

    [Fact]
    public async Task RestartSession_AuthorityStoppedJournalFailure_DoesNotStartReplacement()
    {
        var sessionId = new AgentSessionId("restart-stopped-journal-failure");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var failure = new IOException("Stopped journal could not be persisted.");

        Assert.Same(failure, await Record.ExceptionAsync(() => factory.RestartExactAsync(
            sessionId,
            original.AgentChat,
            null,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None,
            _ => ValueTask.FromException(failure),
            CancellationToken.None)));

        Assert.True(original.LocalAgentChat.IsTerminal);
        Assert.Empty(factory.RunningSessions);
        await Assert.ThrowsAsync<IOException>(() => factory.GetAsync(sessionId));
    }

    [Fact]
    public async Task RestartSession_AuthorityStoppedJournal_PrecedesReplacementStartup()
    {
        var sessionId = new AgentSessionId("restart-stopped-journal-order");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var stopped = false;

        await using var replacement = await factory.RestartExactAsync(
            sessionId,
            original.AgentChat,
            null,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None,
            _ =>
            {
                Assert.True(original.LocalAgentChat.IsTerminal);
                Assert.Empty(factory.RunningSessions);
                stopped = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(stopped);
        Assert.NotSame(original.AgentChat, replacement.AgentChat);
    }

    [Fact]
    public async Task RestartSession_PreservesPerSessionServicesInsteadOfFactoryDefaults()
    {
        var sessionId = new AgentSessionId("restart-per-session-services");
        var sessionContext = new CurrentSessionContext
        {
            AgentSessionId = sessionId.Value,
            OwningProfileEntityId = "specific-owner",
            OwnershipGeneration = 7,
            RuntimeEpoch = new Phantom.Workspaces.Llm.Remote.RuntimeEpoch { Value = Guid.NewGuid() },
        };
        var sessionClient = new DeterministicTestChatClient();
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(
            sessionId,
            EchoAgentDefinition,
            new AgentServices
            {
                CurrentSessionContext = sessionContext,
                ChatClientOverride = sessionClient,
                LogChat = true,
            });
        await using var replacement = await factory.RestartExactAsync(
            sessionId,
            original.AgentChat,
            null,
            (_, _) => ValueTask.CompletedTask);
        var replacementServices = GetRequestServices(replacement.LocalAgentChat);
        Assert.Same(sessionContext, replacementServices.CurrentSessionContext);
        Assert.Same(sessionClient, replacementServices.ChatClientOverride);
        Assert.True(replacementServices.LogChat);
        Assert.Same(factory, replacementServices.RunningAgentChatFactory);
    }

    [Fact]
    public async Task RestartSession_CancelledBeforeCommitPreservesOriginalQueueAndResources()
    {
        var sessionId = new AgentSessionId("restart-cancel-before-commit");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var held = original.LocalAgentChat.QueueManager.CreateInputQueue("Pending", AgentInputQueueImmediacy.Held);
        original.LocalAgentChat.EnqueueUserMessage("keep", held);
        var revision = original.AgentChat.InputQueues.Snapshot.Revision;
        using var caller = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) =>
            {
                caller.Cancel();
                return ValueTask.CompletedTask;
            }, caller.Token));
        Assert.Same(original.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
        Assert.Equal(revision, original.AgentChat.InputQueues.Snapshot.Revision);
        original.LocalAgentChat.EnqueueUserMessage("still usable", held);
        Assert.Equal(2, held.Queue.Items.Count);
    }

    [Fact]
    public async Task RestartSession_StartupFailureRetainsCheckpointForControlledStoppedRetry()
    {
        var sessionId = new AgentSessionId("restart-controlled-retry");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var held = original.LocalAgentChat.QueueManager.CreateInputQueue("Pending", AgentInputQueueImmediacy.Held);
        original.LocalAgentChat.EnqueueUserMessage("survives failure", held);
        var client = new Moq.Mock<IChatClient>();
        client.Setup(value => value.GetService(Moq.It.IsAny<Type>(), Moq.It.IsAny<object?>()))
            .Throws(new IOException("replacement backend unavailable"));
        await Assert.ThrowsAsync<IOException>(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, new AgentServices { ChatClientOverride = client.Object },
            (_, _) => ValueTask.CompletedTask));
        Assert.Empty(factory.RunningSessions);
        await Assert.ThrowsAsync<IOException>(() => factory.GetAsync(sessionId));
        await using var replacement = await factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => ValueTask.CompletedTask);
        Assert.Equal("survives failure", Assert.Single(
            replacement.AgentChat.InputQueues.Snapshot.Queues.Single(queue => queue.Name == "Pending").Items)
            .Messages[0].Text);
        Assert.False(replacement.LocalAgentChat.InputQueueManager.TryDequeueNextImmediateOrQueued(out _));
        Assert.Same(replacement.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => throw new InvalidOperationException("must not be called")));
    }

    [Fact]
    public async Task RestartSession_PreservesQueueAndBlocksAcquisitionUntilOldResourcesStop()
    {
        var sessionId = new AgentSessionId("restart-owner-boundary");
        await using var factory = CreateFactory();
        await using var first = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        await using var second = await factory.GetAsync(sessionId);
        var held = first.LocalAgentChat.QueueManager.CreateInputQueue("Pending", AgentInputQueueImmediacy.Held);
        first.LocalAgentChat.EnqueueUserMessage("retain input", held);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            disposing.TrySetResult();
            return new ValueTask(release.Task);
        });
        first.LocalAgentChat.RegisterOwnedResource(resource.Object);
        AgentRuntimeQueueCheckpoint? durable = null;
        var restart = factory.RestartExactAsync(sessionId, first.AgentChat, null,
            (checkpoint, _) =>
            {
                durable = checkpoint;
                return ValueTask.CompletedTask;
            });
        await disposing.Task;
        var acquire = factory.GetAsync(sessionId);
        Assert.False(acquire.IsCompleted);
        Assert.False(restart.IsCompleted);
        release.TrySetResult();
        await using var replacement = await restart;
        await using var attached = await acquire;
        Assert.NotSame(first.AgentChat, replacement.AgentChat);
        Assert.Same(replacement.AgentChat, attached.AgentChat);
        Assert.Equal("retain input", Assert.Single(
            replacement.AgentChat.InputQueues.Snapshot.Queues.Single(queue => queue.Name == "Pending").Items)
            .Messages[0].Text);
        Assert.Equal(durable!.InputQueues.Revision, replacement.AgentChat.InputQueues.Snapshot.Revision);
        Assert.False(replacement.LocalAgentChat.InputQueueManager.TryDequeueNextImmediateOrQueued(out _));
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Same(replacement.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [Fact]
    public async Task RestartSession_CheckpointPersistenceFailureLeavesOriginalUsable()
    {
        var sessionId = new AgentSessionId("restart-persist-failure");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var error = new IOException("checkpoint unavailable");
        Assert.Same(error, await Record.ExceptionAsync(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => ValueTask.FromException(error))));
        Assert.Same(original.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
        var held = original.LocalAgentChat.QueueManager.CreateInputQueue("Still usable", AgentInputQueueImmediacy.Held);
        original.LocalAgentChat.EnqueueUserMessage("unchanged", held);
        Assert.Single(held.Queue.Items);
    }

    [Fact]
    public async Task RestartSession_CancelledAfterCommitCompletesFullCleanupAndReplacement()
    {
        var sessionId = new AgentSessionId("restart-cancel-after-commit");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        using var caller = new CancellationTokenSource();
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            caller.Cancel();
            return ValueTask.CompletedTask;
        });
        original.LocalAgentChat.RegisterOwnedResource(resource.Object);
        await using var replacement = await factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => ValueTask.CompletedTask, caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.NotSame(original.AgentChat, replacement.AgentChat);
        Assert.Same(replacement.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
    }

    [Fact]
    public async Task RestartSession_OldShutdownFailureNeverStartsReplacement()
    {
        var sessionId = new AgentSessionId("restart-stop-failure");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(ValueTask.FromException(new IOException("cannot stop")));
        original.LocalAgentChat.RegisterOwnedResource(resource.Object);
        await Assert.ThrowsAsync<AggregateException>(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => ValueTask.CompletedTask));
        await Assert.ThrowsAsync<AggregateException>(() => factory.GetAsync(sessionId));
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_ConcurrentDirectDisposalWaitsForCompleteOldRuntimeShutdown()
    {
        var sessionId = new AgentSessionId("restart-concurrent-dispose");
        await using var factory = CreateFactory();
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            disposing.TrySetResult();
            return new ValueTask(release.Task);
        });
        original.LocalAgentChat.RegisterOwnedResource(resource.Object);
        var directDisposal = original.LocalAgentChat.DisposeAsync().AsTask();
        await disposing.Task;
        var restart = factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) => ValueTask.CompletedTask);
        Assert.False(restart.IsCompleted);
        release.TrySetResult();
        await directDisposal;
        await using var replacement = await restart;
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
        Assert.NotSame(original.AgentChat, replacement.AgentChat);
    }

    [Fact]
    public async Task RestartSession_FinalPersistenceFailureBlocksReplacementAfterFullShutdown()
    {
        var sessionId = new AgentSessionId("restart-final-persistence");
        var memory = new InMemoryAgentPersistenceStore();
        var failWrites = false;
        var store = new Moq.Mock<IAgentPersistenceStore>();
        store.Setup(value => value.StoreAsync(Moq.It.IsAny<StoreRequestAgent>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((StoreRequestAgent request, CancellationToken token) =>
                failWrites ? ValueTask.FromException(new IOException("final persistence failed"))
                    : memory.StoreAsync(request, token));
        store.Setup(value => value.RestoreAsync(Moq.It.IsAny<RestoreRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((RestoreRequest request, CancellationToken token) => memory.RestoreAsync(request, token));
        store.Setup(value => value.ReadMessagesAsync(Moq.It.IsAny<ReadMessagesRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((ReadMessagesRequest request, CancellationToken token) => memory.ReadMessagesAsync(request, token));
        store.Setup(value => value.ReadSubAgentChildIdsAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string persistedSessionId, CancellationToken token) => memory.ReadSubAgentChildIdsAsync(persistedSessionId, token));
        await using var factory = CreateFactory(store.Object);
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        await Assert.ThrowsAsync<IOException>(() => factory.RestartExactAsync(
            sessionId, original.AgentChat, null, (_, _) =>
            {
                failWrites = true;
                return ValueTask.CompletedTask;
            }));
        await Assert.ThrowsAsync<IOException>(() => factory.GetAsync(sessionId));
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_FinalPersistenceWaitsForActualProviderDisposal()
    {
        var sessionId = new AgentSessionId("restart-provider-disposal");
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse(isDisposalReady: false);
        var memory = new InMemoryAgentPersistenceStore();
        var restarting = false;
        var store = new Moq.Mock<IAgentPersistenceStore>();
        store.Setup(value => value.StoreAsync(Moq.It.IsAny<StoreRequestAgent>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((StoreRequestAgent request, CancellationToken token) =>
            {
                if (restarting)
                    Assert.True(stream.WaitForDisposalAsync().IsCompleted);
                return memory.StoreAsync(request, token);
            });
        store.Setup(value => value.RestoreAsync(Moq.It.IsAny<RestoreRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((RestoreRequest request, CancellationToken token) => memory.RestoreAsync(request, token));
        store.Setup(value => value.ReadMessagesAsync(Moq.It.IsAny<ReadMessagesRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((ReadMessagesRequest request, CancellationToken token) => memory.ReadMessagesAsync(request, token));
        store.Setup(value => value.ReadSubAgentChildIdsAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string persistedSessionId, CancellationToken token) => memory.ReadSubAgentChildIdsAsync(persistedSessionId, token));
        await using var factory = CreateFactory(store.Object, client);
        await using var original = await factory.GetOrCreateAsync(sessionId, EchoAgentDefinition);
        original.LocalAgentChat.EnqueueUserMessage("in flight, never replay");
        await stream.WaitForClaimedAsync();
        AgentRuntimeQueueCheckpoint? checkpoint = null;
        var restart = factory.RestartExactAsync(sessionId, original.AgentChat, null, (state, _) =>
        {
            restarting = true;
            checkpoint = state;
            return ValueTask.CompletedTask;
        });
        try
        {
            await stream.WaitForDisposalStartedAsync();
        }
        finally
        {
            stream.ReleaseDisposal();
        }
        await using var replacement = await restart;
        Assert.True(checkpoint!.HasInterruptedWork);
        Assert.Contains(replacement.AgentChat.History, item =>
            item.Role == AgentChatHistoryItem.DiagnosticChatRole
            && item.Contents.OfType<TextContent>().Any(content =>
                content.Text.Contains("Tool side effects may already have occurred", StringComparison.Ordinal)));
        Assert.All(replacement.AgentChat.InputQueues.Snapshot.Queues, queue => Assert.Empty(queue.Items));
        Assert.Equal(1, stream.DisposalCount);
    }

    [Fact]
    public async Task GetAsync_NoBroker_OptionalCredentialDoesNotPreventFirstAnswer()
    {
        var environment = new Core.Tests.McpCredentialTestEnvironment();
        var client = Core.Tests.McpCredentialTestEnvironment.Client();
        await using var factory = CreateFactory(client: client);
        await using var lease = await factory.GetOrCreateAsync(
            new AgentSessionId("no-broker-optional"),
            Core.Tests.McpCredentialTestEnvironment.Definition(),
            environment.Services(client) with { AccessBroker = null });
        await Core.Tests.McpCredentialTestEnvironment.AssertUsableUnavailableAsync(
            lease.LocalAgentChat, client, environment, expectedRequests: 0);
        Assert.Single(factory.RunningSessions);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("denied")]
    [InlineData("cancelled")]
    [InlineData("reacquisition")]
    [InlineData("authorization")]
    public async Task GetAsync_McpCredentialIsUnavailable_ChatStaysUsableAndServerIsUnavailable(string outcome)
    {
        var environment = new Core.Tests.McpCredentialTestEnvironment
        {
            Acquire = (_, _) => Task.FromResult(Core.Tests.McpCredentialTestEnvironment.Failure(outcome)),
        };
        var client = Core.Tests.McpCredentialTestEnvironment.Client();
        await using var factory = CreateFactory(client: client);
        await using var lease = await factory.GetOrCreateAsync(
            new AgentSessionId("optional-" + outcome),
            Core.Tests.McpCredentialTestEnvironment.Definition(),
            environment.Services(client));
        await Core.Tests.McpCredentialTestEnvironment.AssertUsableUnavailableAsync(lease.LocalAgentChat, client, environment);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("reacquisition")]
    [InlineData("authorization")]
    public async Task GetAsync_RequiredCoreCredentialIsUnavailable_FailsClosed(string outcome)
    {
        var environment = new Core.Tests.McpCredentialTestEnvironment
        {
            Acquire = (_, _) => Task.FromResult(Core.Tests.McpCredentialTestEnvironment.Failure(outcome)),
        };
        await using var factory = CreateFactory();
        var exception = await Record.ExceptionAsync(() => factory.GetOrCreateAsync(
            new AgentSessionId("required-" + outcome),
            Core.Tests.McpCredentialTestEnvironment.Definition(required: true),
            environment.Services(Core.Tests.McpCredentialTestEnvironment.Client())));
        Assert.True(exception is SecretMaterializationRefusedException or SecretMaterializationFailedException);
        Assert.Empty(factory.RunningSessions);
        Assert.Equal("Required", Assert.Single(environment.Requests).Reference.Name);
        Assert.Empty(environment.HttpRequests);
    }
    [Theory]
    [InlineData(AgentChatCompletionState.Unknown)]
    [InlineData(AgentChatCompletionState.Running)]
    public void Activity_UnmaterializedDescendant_DoesNotRequireAChildEditor(AgentChatCompletionState state)
    {
        var child = new SubAgent(new AgentSessionId("unmaterialized"), factory: null);
        child.SetRestoredCompletionState(state);
        var queues = new Moq.Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot { Revision = 0, Queues = [] });
        var root = new Moq.Mock<IAgentChat>();
        root.SetupGet(value => value.HistoryPopulated).Returns(Task.CompletedTask);
        root.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        root.SetupGet(value => value.Modals).Returns(new ReadOnlyObservableCollection<AgentChatModal>([]));
        root.SetupGet(value => value.InputQueues).Returns(queues.Object);
        root.SetupGet(value => value.SubAgents).Returns(new ReadOnlyObservableCollection<IRunningSubAgent>([child]));
        Assert.Null(child.AgentChat);
        Assert.True(AgentChatActivity.HasPendingWork(root.Object));
    }

    [Fact]
    public async Task IdleRelease_MaterializedDescendantActivity_FencesAndTerminatesTheWholeRuntimeTree()
    {
        await using var factory = CreateFactory();
        await using var parent = await factory.GetOrCreateAsync(new AgentSessionId("parent-tree"), EchoAgentDefinition);
        await using var child = await factory.GetOrCreateAsync(new AgentSessionId("child-tree"), EchoAgentDefinition,
            registerAsRunningAgent: false);
        await ((ISubAgentTable)parent.LocalAgentChat).Add(child.LocalAgentChat);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        child.LocalAgentChat.RegisterOwnedResource(resource.Object);
        var held = child.LocalAgentChat.QueueManager.CreateInputQueue("Held child", AgentInputQueueImmediacy.Held);
        child.LocalAgentChat.EnqueueUserMessage("child pending work", held);
        Assert.False(await parent.TryPrepareIdleCloseAsync());
        Assert.True(await factory.TerminateExactAsync(parent.SessionId, parent.AgentChat));
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [Fact]
    public async Task IdleRelease_SubmissionWinsOwnerGate_RequiresConsent()
    {
        var id = new AgentSessionId("idle-release-submission");
        await using var factory = CreateFactory();
        await using var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        var held = lease.LocalAgentChat.QueueManager.CreateInputQueue("Held", AgentInputQueueImmediacy.Held);
        lease.LocalAgentChat.EnqueueUserMessage("queued before release", held);
        Assert.False(await lease.TryPrepareIdleCloseAsync());
        Assert.Single(factory.RunningSessions);
        Assert.Single(held.Queue.Items);
    }

    [Fact]
    public async Task IdleRelease_ReleaseWinsOwnerGate_RejectsLateSubmissionAndDisposesOnce()
    {
        var id = new AgentSessionId("idle-release-first");
        await using var factory = CreateFactory();
        var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        Assert.True(await lease.TryPrepareIdleCloseAsync());
        Assert.Empty(factory.RunningSessions);
        Assert.Throws<ObjectDisposedException>(() => lease.LocalAgentChat.EnqueueUserMessage("too late"));
        await using var child = await factory.GetOrCreateAsync(new AgentSessionId("late-child"), EchoAgentDefinition,
            registerAsRunningAgent: false);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ((ISubAgentTable)lease.LocalAgentChat).Add(child.LocalAgentChat));
        await lease.DisposeAsync();
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task IdleRelease_OtherViewerRemains_DoesNotSealSharedRuntime()
    {
        var id = new AgentSessionId("idle-release-shared");
        await using var factory = CreateFactory();
        await using var first = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        await using var second = await factory.GetAsync(id);
        Assert.True(await first.TryPrepareIdleCloseAsync());
        var held = second.LocalAgentChat.QueueManager.CreateInputQueue("Held", AgentInputQueueImmediacy.Held);
        second.LocalAgentChat.EnqueueUserMessage("survives", held);
        Assert.Single(held.Queue.Items);
        Assert.Single(factory.RunningSessions);
    }

    [Fact]
    public async Task ExactRuntime_OldLeaseAndDecision_CannotReleaseOrTerminateReplacement()
    {
        var id = new AgentSessionId("replacement");
        await using var factory = CreateFactory();
        var old = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        Assert.True(await factory.TerminateExactAsync(id, old.AgentChat));
        await using var replacement = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        Assert.False(await factory.TerminateExactAsync(id, old.AgentChat));
        Assert.False(await old.TryPrepareIdleCloseAsync());
        await old.DisposeAsync();
        Assert.Single(factory.RunningSessions);
        Assert.Same(replacement.AgentChat, factory.RunningSessions.Single().AgentChat);
    }

    [Fact]
    public async Task ExactRuntime_ConcurrentTermination_WaitsForTheSameCleanupBeforeReplacement()
    {
        var id = new AgentSessionId("concurrent-termination");
        await using var factory = CreateFactory();
        await using var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            disposing.TrySetResult();
            return new ValueTask(release.Task);
        });
        lease.LocalAgentChat.RegisterOwnedResource(resource.Object);
        var first = factory.TerminateExactAsync(id, lease.AgentChat);
        await disposing.Task;
        var second = factory.TerminateExactAsync(id, lease.AgentChat);
        var replacement = factory.GetOrCreateAsync(id, EchoAgentDefinition);
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(replacement.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.True(await second);
        await using var current = await replacement;
        Assert.NotSame(lease.AgentChat, current.AgentChat);
        Assert.Same(current.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactRuntime_FailedCleanup_BlocksAcquisitionAndEveryTerminationWaiter(bool explicitTermination)
    {
        var id = new AgentSessionId("failed-exact-cleanup");
        await using var factory = CreateFactory();
        await using var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(async () =>
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("owned cleanup failed");
        });
        lease.LocalAgentChat.RegisterOwnedResource(resource.Object);
        Task first = explicitTermination
            ? factory.TerminateExactAsync(id, lease.AgentChat)
            : lease.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = factory.TerminateExactAsync(id, lease.AgentChat);
        var acquisition = factory.GetOrCreateAsync(id, EchoAgentDefinition);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        await Assert.ThrowsAnyAsync<Exception>(() => acquisition);
        await Assert.ThrowsAnyAsync<Exception>(() => factory.GetAsync(id));
        Assert.Empty(factory.RunningSessions);
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [Fact]
    public async Task AcquisitionProgress_ConstructionHook_ObservesBeforeLeasePublication()
    {
        var id = new AgentSessionId("progress-construction");
        await using var factory = CreateFactory();
        IAgentChat? initializing = null;
        using var progress = new AgentChatAcquisitionProgress(id.Value!, chat =>
        {
            initializing = chat;
            Assert.Empty(factory.RunningSessions);
            Assert.False(chat.HistoryPopulated.IsCompleted);
        });
        await using var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        Assert.Same(initializing, lease.AgentChat);
    }

    [Fact]
    public async Task AcquisitionProgress_AuthorizationActions_AreBoundToExactChatAndRemovedOnCompletion()
    {
        var id = new AgentSessionId("progress-actions");
        await using var factory = CreateFactory();
        using var cancellation = new CancellationTokenSource();
        var resumes = 0;
        using var progress = new AgentChatAcquisitionProgress(id.Value!, _ => { }, cancellation.Cancel, cancellation.Token);
        await using var lease = await factory.GetOrCreateAsync(id, EchoAgentDefinition);
        var wait = await AgentChatAcquisitionProgress.WaitingForAuthorizationAsync("<GitHub>", _ =>
        {
            resumes++;
            return Task.CompletedTask;
        });
        var item = Assert.Single(lease.AgentChat.RunningItems);
        var text = Assert.IsType<TextContent>(Assert.Single(Assert.Single(item.Items).Contents)).Text!;
        Assert.DoesNotContain("https:", text);
        var href = System.Text.RegularExpressions.Regex.Match(text, @"phantom-startup:[a-f0-9]+").Value;
        Assert.True(await AgentStartupActions.TryInvokeAsync(lease.AgentChat, href));
        Assert.Equal(1, resumes);
        await using var other = await factory.GetOrCreateAsync(new AgentSessionId("other-actions"), EchoAgentDefinition);
        Assert.True(await AgentStartupActions.TryInvokeAsync(other.AgentChat, href));
        Assert.Equal(1, resumes);
        await wait!.DisposeAsync();
        Assert.Empty(lease.AgentChat.RunningItems);
        Assert.True(await AgentStartupActions.TryInvokeAsync(lease.AgentChat, href));
        Assert.Equal(1, resumes);
        Assert.False(await AgentStartupActions.TryInvokeAsync(lease.AgentChat, "https://example.com"));
    }

    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    private static async Task<InMemoryAgentPersistenceStore> CreatePopulatedStoreAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null)
    {
        var store = new InMemoryAgentPersistenceStore();
        var def = definition ?? EchoAgentDefinition;
        var definitionJson = BsonDocument.Parse(def.ToJson());
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId.Value!,
                AgentDefinitionJson = definitionJson,
            }
        });
        return store;
    }

    private static AgentChatFactory CreateFactory(
        IAgentPersistenceStore? store = null,
        DeterministicTestChatClient? client = null,
        TaskScheduler? foregroundScheduler = null)
    {
        store ??= new InMemoryAgentPersistenceStore();
        client ??= new DeterministicTestChatClient();
        var services = new AgentServices { ChatClientOverride = client };
        return new AgentChatFactory(store, services, foregroundScheduler ?? TaskScheduler.Default);
    }

    [Fact]
    public async Task GetAsync_NewSession_LoadsFromPersistenceAndReturnsLease()
    {
        var sessionId = new AgentSessionId("session-1");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.Equal(sessionId, lease.SessionId);
        Assert.NotNull(lease.LocalAgentChat);
    }

    [Fact]
    public async Task GetAsync_NewSession_AppearsInRunningSessions()
    {
        var sessionId = new AgentSessionId("session-2");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
    }

    [Fact]
    public async Task GetAsync_SessionAlreadyRunning_ReturnsSameAgentChatNewLease()
    {
        var sessionId = new AgentSessionId("session-3");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease1 = await factory.GetAsync(sessionId);
        await using var lease2 = await factory.GetAsync(sessionId);

        Assert.NotSame(lease1, lease2);
        Assert.Same(lease1.AgentChat, lease2.AgentChat);
        Assert.Single(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_AfterLastLeaseDisposed_RemovesFromRunningSessions()
    {
        var sessionId = new AgentSessionId("session-4");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        var lease = await factory.GetAsync(sessionId);
        Assert.Single(factory.RunningSessions);

        await lease.DisposeAsync();

        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_AfterLastLeaseDisposed_DisposesAgentChat()
    {
        var sessionId = new AgentSessionId("session-5");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var trackingClient = new DisposalTrackingChatClient();
        var services = new AgentServices { ChatClientOverride = trackingClient };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        var lease = await factory.GetAsync(sessionId);
        Assert.False(trackingClient.Disposed);

        await lease.DisposeAsync();

        Assert.True(trackingClient.Disposed);
    }

    [Fact]
    public async Task GetAsync_MultipleLeases_RemovedOnlyAfterAllDisposed()
    {
        var sessionId = new AgentSessionId("session-6");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        var lease1 = await factory.GetAsync(sessionId);
        var lease2 = await factory.GetAsync(sessionId);

        await lease1.DisposeAsync();
        Assert.Single(factory.RunningSessions);

        await lease2.DisposeAsync();
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_ConcurrentFirstAcquire_OnlyCreatesOneAgentChat()
    {
        var sessionId = new AgentSessionId("session-7");
        var countingStore = new CountingPersistenceStore(await CreatePopulatedStoreAsync(sessionId));
        await using var factory = CreateFactory(store: countingStore);

        var task1 = factory.GetAsync(sessionId);
        var task2 = factory.GetAsync(sessionId);
        var leases = await Task.WhenAll(task1, task2);

        Assert.Equal(1, countingStore.RestoreAsyncCallCount);
        Assert.Same(leases[0].AgentChat, leases[1].AgentChat);

        await leases[0].DisposeAsync();
        await leases[1].DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_WithDisplayNameOverride_PopulatesAgentChatDisplayName()
    {
        // Fix #1133: AgentChatFactory.CreateAsync must forward the displayNameOverride /
        // descriptionOverride arguments into the InternalCreateAgentChatRequest so the newly
        // created AgentChat's DisplayName/Description reflect the caller-supplied values.
        var sessionId = new AgentSessionId("session-1133-with");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(
            EchoAgentDefinition,
            sessionId,
            services: null,
            displayNameOverride: "fix-reload1",
            descriptionOverride: "reload the workspace");

        Assert.Equal("fix-reload1", lease.LocalAgentChat.DisplayName);
        Assert.Equal("reload the workspace", lease.LocalAgentChat.Description);
    }

    [Fact]
    public async Task CreateAsync_WithoutDisplayNameOverride_UsesClientInfoDefault()
    {
        // Fix #1133 fallback: with no override provided, AgentChat.DisplayName degrades to the
        // client-info default (never throws, never leaks the session GUID) so pre-existing
        // callers that do not opt into the new arguments retain their previous behaviour.
        var sessionId = new AgentSessionId("session-1133-without");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        // No override was supplied ⇒ DisplayName falls back to the empty client-info default,
        // and — critically — does NOT equal the session id (which was the observed #1133 bug).
        Assert.NotEqual(sessionId.Value, lease.LocalAgentChat.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_PersistsSessionBeforeReturning()
    {
        var sessionId = new AgentSessionId("session-8");
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        var persisted = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = sessionId.Value! });
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.Value.AgentDefinitionJson);
    }

    [Fact]
    public async Task CreateAsync_NewSession_AppearsInRunningSessions()
    {
        var sessionId = new AgentSessionId("session-9");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateSessionId_ThrowsInvalidOperationException()
    {
        var sessionId = new AgentSessionId("session-10");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(EchoAgentDefinition, sessionId));
    }

    [Fact]
    public async Task RunningSessions_Add_HappensOnForegroundScheduler()
    {
        var sessionId = new AgentSessionId("session-11");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var scheduler = new VerifyingScheduler();
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task RunningSessions_Remove_HappensOnForegroundScheduler()
    {
        var sessionId = new AgentSessionId("session-12");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var scheduler = new VerifyingScheduler();
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        var lease = await factory.GetAsync(sessionId);
        scheduler.WasInvoked = false;

        await lease.DisposeAsync();

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesAllSessions()
    {
        var id1 = new AgentSessionId("session-13a");
        var id2 = new AgentSessionId("session-13b");
        var trackingClient1 = new DisposalTrackingChatClient();
        var trackingClient2 = new DisposalTrackingChatClient();

        var store = new InMemoryAgentPersistenceStore();
        var def1 = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        var def2 = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id1.Value!, AgentDefinitionJson = def1 } });
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id2.Value!, AgentDefinitionJson = def2 } });

        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient1 },
            TaskScheduler.Default);

        var lease1 = await factory.GetAsync(id1);
        factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient2 },
            TaskScheduler.Default);
        var lease2 = await factory.GetAsync(id2);

        // Dispose second factory (which owns lease2's session)
        await factory.DisposeAsync();

        Assert.True(trackingClient2.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_AllOutstandingChats_Disposed()
    {
        var id1 = new AgentSessionId("session-14a");
        var id2 = new AgentSessionId("session-14b");
        var store = new InMemoryAgentPersistenceStore();
        var trackingClient = new DisposalTrackingMultipleClient();

        var def = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id1.Value!, AgentDefinitionJson = def } });
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id2.Value!, AgentDefinitionJson = def } });

        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient },
            TaskScheduler.Default);

        var lease1 = await factory.GetAsync(id1);
        var lease2 = await factory.GetAsync(id2);

        await factory.DisposeAsync();

        Assert.Equal(2, trackingClient.DisposeCount);
    }

    // ── Foreground-context affinity (issue #909) ─────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-1");
        var store = await CreatePopulatedStoreAsync(sessionId);
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        // Callers such as agent tools invoke the factory from thread-pool threads; the factory
        // must schedule chat creation onto its foreground scheduler so the AgentChat
        // constructor's affinity verification is satisfied.
        await using var lease = await Task.Run(() => factory.GetOrCreateAsync(sessionId));

        Assert.NotNull(lease.LocalAgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.LocalAgentChat));
    }

    [Fact]
    public async Task GetAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-2");
        var store = await CreatePopulatedStoreAsync(sessionId);
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        await using var lease = await Task.Run(() => factory.GetAsync(sessionId));

        Assert.NotNull(lease.LocalAgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.LocalAgentChat));
    }

    [Fact]
    public async Task CreateAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-3");
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(foregroundScheduler: scheduler);

        await using var lease = await Task.Run(() => factory.CreateAsync(EchoAgentDefinition, sessionId));

        Assert.NotNull(lease.LocalAgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.LocalAgentChat));
    }

    private static TaskScheduler? GetForegroundScheduler(AgentChat chat)
    {
        var field = typeof(AgentChat).GetField(
            "foregroundScheduler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return (TaskScheduler?)field!.GetValue(chat);
    }

    // ── Factory self-injection (issue #1036) ─────────────────────────────────
    //
    // When the factory hands its services to the chats it creates, it must inject
    // itself as the RunningAgentChatFactory. Otherwise a reopened session cannot
    // materialise its persisted sub-agents (RestoreSubAgentsAsync bails out when the
    // factory is null), so the sub-agents tree comes back empty.

    private static AgentServices GetRequestServices(AgentChat chat)
    {
        var requestField = typeof(AgentChat).GetField(
            "request",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(requestField);
        var request = requestField!.GetValue(chat);
        Assert.NotNull(request);

        var servicesProperty = request!.GetType().GetProperty("AgentServices");
        Assert.NotNull(servicesProperty);
        var services = (AgentServices?)servicesProperty!.GetValue(request);
        Assert.NotNull(services);
        return services!;
    }

    private static async Task WaitForSubAgentCountAsync(AgentChat chat, int expected)
    {
        if (chat.SubAgents.Count >= expected)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (chat.SubAgents.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        var incc = (INotifyCollectionChanged)chat.SubAgents;
        incc.CollectionChanged += Handler;
        try
        {
            if (chat.SubAgents.Count >= expected)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            incc.CollectionChanged -= Handler;
        }
    }

    private static async Task StoreChildAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        string childSessionId)
    {
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson()),
            }
        });
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);
    }

    [Fact]
    public async Task GetAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-get");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        var services = GetRequestServices(lease.LocalAgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetOrCreateAsync_ManifestRequest_ExposesFactoryAsRunningAgentChatFactory()
    {
        // Issue #1180 regression pin: the manifest-open path in AgentManifestLaunchpadViewModel
        // resolves an AgentDefinition from an AgentManifest (via RunningAgentChatTable) and then
        // calls AgentChatFactory.GetOrCreateAsync with bare AgentServices (no RunningAgentChatFactory
        // pre-set). The factory must self-inject via WithSelfAsFactory so AgentServices reaching
        // AgentChat.CreateAsync carries the factory — otherwise the #1109 guard in AgentChat
        // throws "must be supplied at construction time" as soon as a Copilot SDK client resolves,
        // which is exactly the crash reported in #1180.
        var sessionId = new AgentSessionId("session-manifest-1180");
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        var bareServices = new AgentServices
        {
            ChatClientOverride = new DeterministicTestChatClient(),
            // Intentionally not setting RunningAgentChatFactory — the factory MUST self-inject.
        };
        Assert.Null(bareServices.RunningAgentChatFactory);

        await using var lease = await factory.GetOrCreateAsync(
            sessionId,
            definition: EchoAgentDefinition,
            services: bareServices);

        var services = GetRequestServices(lease.LocalAgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-getorcreate");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId);

        var services = GetRequestServices(lease.LocalAgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenRegisterAsRunningAgentFalse_DoesNotAppearInRunningSessions()
    {
        // Issue #1150: sub-agents dispatched via SubAgentDispatcherChatClient must not appear in
        // the top-right "Running agents" popup. They opt out via registerAsRunningAgent: false.
        var sessionId = new AgentSessionId("session-noregister");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId, registerAsRunningAgent: false);

        Assert.NotNull(lease.LocalAgentChat);
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenRegisterAsRunningAgentTrue_AppearsInRunningSessions()
    {
        // Issue #1150: top-level agents (the default) must still show up in RunningSessions.
        var sessionId = new AgentSessionId("session-register");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId, registerAsRunningAgent: true);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
    }

    [Fact]
    public async Task CreateAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-create");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        var services = GetRequestServices(lease.LocalAgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetAsync_ExplicitFactoryInServices_IsOverwrittenBySelf()
    {
        // Fix #1109: WithSelfAsFactory now ALWAYS injects the outer factory unconditionally.
        // The previous "preserve intentional override" behavior let a foreign factory be wired
        // past the outer factory's sub-agent lifecycle bookkeeping — the same silent-misroute
        // that #1109/#1110 remove for the null case.
        var sessionId = new AgentSessionId("session-selffactory-explicit");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var explicitFactory = CreateFactory(store: store);
        await using var _ = explicitFactory;
        var services = new AgentServices
        {
            ChatClientOverride = new DeterministicTestChatClient(),
            RunningAgentChatFactory = explicitFactory,
        };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        await using var lease = await factory.GetAsync(sessionId);

        var chatServices = GetRequestServices(lease.LocalAgentChat);
        Assert.Same(factory, chatServices.RunningAgentChatFactory);
        Assert.NotSame(explicitFactory, chatServices.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetAsync_ResumeSessionWithPersistedSubAgents_RestoresThem()
    {
        var parentSessionId = "session-selffactory-resume";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildAsync(store, parentSessionId, "resume-child-a");
        await StoreChildAsync(store, parentSessionId, "resume-child-b");

        // Bare services (no RunningAgentChatFactory): the factory must self-inject so
        // the reopened parent can materialise its persisted sub-agents.
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));

        await WaitForSubAgentCountAsync(lease.LocalAgentChat, 2);
        Assert.Equal(2, lease.LocalAgentChat.SubAgents.Count);
    }

    // ── Issue #1186: restore-time null-model resilience ──────────────────────
    //
    // Regression: a startup restore of a default workspace whose parent agent chat
    // has persisted sub-agents with empty AgentDefinitions (Model == null) used to
    // throw "Agent definition does not specify a model." from AgentFactory during
    // MarkRestoredSubAgentTerminalAsync's eager materialisation. The exception
    // originated from a foreground-scheduled construction path and prevented the
    // parent chat's InitializeAsync from returning, hanging the startup splash
    // (LoadingWindow.Close() at App.axaml.cs:310 never ran).
    //
    // The fix: MarkRestoredSubAgentTerminalAsync no longer materialises the child.
    // It records the terminal state on the SubAgent stub via
    // SetRestoredCompletionState; the state applies lazily when a later
    // AcquireLeaseAsync actually needs the child chat.

    private const string EmptyPersistedAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "empty-persisted-child"
        }
        """;

    private static async Task StoreChildWithDefinitionJsonAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        string childSessionId,
        string persistedAgentDefinitionJson)
    {
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = BsonDocument.Parse(persistedAgentDefinitionJson),
            }
        });
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);
    }

    [Fact]
    public async Task MarkRestoredSubAgentTerminal_EmptyPersistedAgentDefinition_DoesNotThrow()
    {
        // #1186 regression guard: restoring a parent whose persisted child has an
        // empty (Model == null) AgentDefinition must complete AgentChat.InitializeAsync
        // successfully, not hang or throw.
        var parentSessionId = "parent-1186-restore-nothrow";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-empty",
            EmptyPersistedAgentDefinitionJson);

        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));

        await WaitForSubAgentCountAsync(lease.LocalAgentChat, 1);
        Assert.Single(lease.LocalAgentChat.SubAgents);
    }

    [Fact]
    public async Task MarkRestoredSubAgentTerminal_EmptyPersistedAgentDefinition_DoesNotConstructChatClient()
    {
        // #1186 mechanism guard: the restore path must NOT invoke the ChatClient
        // factory for the child (empty AgentDefinition would throw). A spy
        // ChatClientOverride records every construction attempt.
        var parentSessionId = "parent-1186-no-construct";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-empty-no-construct",
            EmptyPersistedAgentDefinitionJson);

        // Use a spy that records every GetService lookup — a proxy for "was any
        // ChatClient ever handed to a materialised child chat?". Since the parent
        // itself uses this client, we count uses AFTER parent construction.
        var spy = new UsageCountingChatClient();
        var services = new AgentServices { ChatClientOverride = spy };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));
        await WaitForSubAgentCountAsync(lease.LocalAgentChat, 1);

        // Snapshot: parent has been constructed. Now if the restore path attempted
        // to materialise the child, an extra AgentChat.InitializeAsync run would
        // pull the client from services.ChatClientOverride and enumerate it.
        var stub = Assert.IsType<SubAgent>(Assert.Single(lease.LocalAgentChat.SubAgents));
        Assert.Null(stub.AgentChat); // child NOT materialised
        Assert.Equal(
            AgentChatCompletionState.Succeeded,
            ((IRunningSubAgent)stub).CompletionState);
    }

    [Fact]
    public async Task RestoreSubAgentsAsync_ChildTerminalTaskFaults_ParentInitializeCompletes()
    {
        // #1186 robustness guard: even if a per-child terminal task were to fault
        // (currently impossible because the task is now synchronous, but this pins
        // the invariant), the parent's InitializeAsync must still return so that
        // startup never hangs.
        var parentSessionId = "parent-1186-child-fault";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-fault-a",
            EmptyPersistedAgentDefinitionJson);
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-fault-b",
            EmptyPersistedAgentDefinitionJson);

        await using var factory = CreateFactory(store: store);

        // Bound the operation aggressively — a hang here is the exact regression
        // #1186 documents.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId), ct: cts.Token);
        await lease.LocalAgentChat.RestoreCompleted.WaitAsync(cts.Token);

        Assert.Equal(2, lease.LocalAgentChat.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChatFactory_GetAsync_ResumeSessionWithEmptyPersistedSubAgents_DoesNotThrow()
    {
        // #1186 top-level regression guard, paralleling
        // GetAsync_ResumeSessionWithPersistedSubAgents_RestoresThem but with empty
        // persisted child AgentDefinitions. The resume must complete without
        // "Agent definition does not specify a model." bubbling up.
        var parentSessionId = "parent-1186-resume-empty";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-resume-empty-a",
            EmptyPersistedAgentDefinitionJson);
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-resume-empty-b",
            EmptyPersistedAgentDefinitionJson);

        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));
        await WaitForSubAgentCountAsync(lease.LocalAgentChat, 2);
        Assert.Equal(2, lease.LocalAgentChat.SubAgents.Count);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class DisposalTrackingChatClient : IChatClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UsageCountingChatClient : IChatClient
    {
        private int _uses;
        public int UseCount => Volatile.Read(ref _uses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _uses);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _uses);
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class DisposalTrackingMultipleClient : IChatClient, IAsyncDisposable
    {
        private int _disposeCount;
        public int DisposeCount => _disposeCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingPersistenceStore : IAgentPersistenceStore
    {
        private readonly IAgentPersistenceStore _inner;
        private int _restoreCount;

        public int RestoreAsyncCallCount => _restoreCount;

        public CountingPersistenceStore(IAgentPersistenceStore inner) => _inner = inner;

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
            => _inner.StoreAsync(request, cancellationToken);

        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _restoreCount);
            return _inner.RestoreAsync(request, cancellationToken);
        }

        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => _inner.ReadMessagesAsync(request, cancellationToken);

        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
            => _inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => _inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
    }

    private sealed class VerifyingScheduler : TaskScheduler
    {
        public bool WasInvoked { get; set; }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        protected override void QueueTask(Task task)
        {
            WasInvoked = true;
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            WasInvoked = true;
            return TryExecuteTask(task);
        }
    }

    // Fix #1187: GetAsync must construct the child AgentChat for a persisted hosted
    // Copilot sub-agent from the rehydrated full AgentDefinition (kind/name/model.provider
    // = github-copilot-subagent) without hitting the "Agent definition does not specify a
    // model." throw.
    [Fact]
    public async Task GetAsync_WithRegisterAsRunningAgentFalse_DoesNotAddToRunningSessions()
    {
        // Issue #1205: sub-agents lazily materialised through the restore path (SubAgent.AcquireLeaseAsync)
        // must not leak into the running-agents flyout as "No Open Tab" rows. GetAsync gains the same
        // opt-out as GetOrCreateAsync did in #1150.
        var sessionId = new AgentSessionId("session-1205-noregister");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId, registerAsRunningAgent: false);

        Assert.NotNull(lease.LocalAgentChat);
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_DefinitionWithSecretPlaceholder_MaterializesBeforeCreatingChatClient()
    {
        // #1405: the GUI foreground factory path (GetOrCreateAsync -> CreateChatOnForegroundAsync)
        // must materialize ${SECRET:...} placeholders — acquiring broker leases and rewriting
        // the definition to an opaque handle — before the chat client is built.
        var provider = new Phantom.Workspaces.Llm.Core.Tests.Secrets.ManifestAccessTestBroker { Material = "resolved-token" };
        var services = new AgentServices
        {
            AccessBroker = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };
        await using var factory = CreateFactory();
        var sessionId = new AgentSessionId("session-materialize-secret");

        await using var lease = await factory.GetOrCreateAsync(sessionId, RequiredSecretDefinition(), services);

        Assert.Single(provider.Requests);
        var json = lease.LocalAgentChat.AgentDefinition!.ToJson();
        Assert.DoesNotContain("${SECRET:GitHubToken}", json, StringComparison.Ordinal);
        Assert.Contains("${SECRET:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_AlreadyMaterializedDefinition_DoesNotRePromptOrReScan()
    {
        // #1405: idempotency — a definition already rewritten to opaque handles (with a resolver
        // attached to services) must not be re-scanned or re-prompted when it flows through the
        // foreground factory path again.
        var provider = new Phantom.Workspaces.Llm.Core.Tests.Secrets.ManifestAccessTestBroker { Material = "resolved-token" };
        var baseServices = new AgentServices
        {
            AccessBroker = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };

        var (materializedDefinition, materializedServices) = await AgentFactory.MaterializeSecretsIfNeededAsync(
            RequiredSecretDefinition(), baseServices, manifest: null, agentSessionId: null, CancellationToken.None);
        Assert.Single(provider.Requests);

        await using var factory = CreateFactory();
        var sessionId = new AgentSessionId("session-already-materialized");

        await using var lease = await factory.GetOrCreateAsync(sessionId, materializedDefinition, materializedServices);

        Assert.Single(provider.Requests);
    }

    private static AgentDefinition RequiredSecretDefinition() => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind":"prompt", "name":"required-secret-agent",
          "model":{"id":"echo","provider":"echo","apiType":"Echo",
            "connection":{"kind":"key","apiKey":"${SECRET:GitHubToken}"}}
        }
        """);

    private static AgentDefinition McpSecretDefinition() => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "mcp-secret-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": [
            {
              "kind": "mcp",
              "name": "github-secret-gated",
              "serverName": "github-secret-gated",
              "connection": { "kind": "key", "endpoint": "http://127.0.0.1:1/", "apiKey": "${SECRET:GitHubToken}" },
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """);

}

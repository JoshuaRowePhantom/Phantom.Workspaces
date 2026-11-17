using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatDisposalTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task FactoryDisposal_PreservesEveryIndependentDeferredSelfTerminationFailure()
    {
        var clients = new[] { new SelfTerminatingClient(true), new SelfTerminatingClient(true) };
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(),
            new AgentServices(), scheduler.ExclusiveScheduler);
        foreach (var client in clients)
        {
            var sessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId(Guid.NewGuid().ToString());
            var lease = await factory.GetOrCreateAsync(sessionId,
                AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
                new AgentServices { ChatClientOverride = client });
            client.Close = () => factory.TerminateExactAsync(sessionId, lease.AgentChat);
            lease.LocalAgentChat.EnqueueUserMessage("close yourself");
        }
        await Task.WhenAll(clients.Select(client => client.Closed.Task));
        var disposing = factory.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            foreach (var client in clients) client.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposing);
            var originals = failure.Flatten().InnerExceptions;
            Assert.Equal(2, originals.Count);
            foreach (var client in clients)
                Assert.Single(originals, error => ReferenceEquals(error, client.Failure));
        }
        finally
        {
            foreach (var client in clients) client.Release.TrySetResult();
            await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task FactorySelfTermination_CancellationFailureStillDrainsAndPreservesIndependentFailures()
    {
        var client = new SelfTerminatingClient(fails: true, cancellationFails: true);
        var resource = new FailureResource(new IOException("independent disposal failure"), client.Record);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(),
            new AgentServices { ChatClientOverride = client }, scheduler.ExclusiveScheduler);
        var sessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId(Guid.NewGuid().ToString());
        var lease = await factory.GetOrCreateAsync(sessionId,
            AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson));
        lease.LocalAgentChat.RegisterOwnedResource(resource);
        client.Close = () => factory.TerminateExactAsync(sessionId, lease.AgentChat);
        lease.LocalAgentChat.EnqueueUserMessage("close yourself");
        await client.Closed.Task;

        var disposing = factory.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, resource.Disposals);
            await client.CancellationInvoked.Task;
            Assert.Equal(1, client.CallbackInvocations);
            Assert.False(client.RegistrationDisposed.Task.IsCompleted);
            client.Record("callback-observed-before-provider-release");
            client.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposing);
            var originals = failure.Flatten().InnerExceptions;
            WriteOwnershipTrace(client, resource, originals);
            Assert.Equal(3, originals.Count);
            Assert.Single(originals, error => ReferenceEquals(error, client.CancellationFailure));
            Assert.Single(originals, error => ReferenceEquals(error, client.Failure));
            Assert.Single(originals, error => ReferenceEquals(error, resource.Failure));
            Assert.Equal(1, resource.Disposals);
        }
        finally
        {
            client.Release.TrySetResult();
            await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task FactorySelfTermination_ProviderUnwindsBeforeCallback_PreservesEveryInvokedFailure()
    {
        var client = new SelfTerminatingClient(fails: true, cancellationFails: true);
        var resource = new FailureResource(new IOException("independent disposal failure"), client.Record);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(),
            new AgentServices { ChatClientOverride = client }, scheduler.ExclusiveScheduler);
        var sessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId(Guid.NewGuid().ToString());
        var lease = await factory.GetOrCreateAsync(sessionId,
            AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson));
        lease.LocalAgentChat.RegisterOwnedResource(resource);
        using var releasePropagation = new ManualResetEventSlim();
        var propagationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration propagationGate = default;
        client.OnRegistered = () =>
        {
            var parent = (CancellationTokenSource)typeof(AgentChat).GetField("cts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(lease.LocalAgentChat)!;
            // Register last on the parent so its CancelAsync worker pauses before linked-token
            // propagation. Cancellation callbacks are synchronous; never block the foreground.
            propagationGate = parent.Token.Register(() =>
            {
                Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                client.Record("parent-propagation-paused");
                propagationEntered.TrySetResult();
                releasePropagation.Wait();
                client.Record("parent-propagation-released");
            });
        };
        client.Close = () => factory.TerminateExactAsync(sessionId, lease.AgentChat);
        Task? disposing = null;
        try
        {
            lease.LocalAgentChat.EnqueueUserMessage("close yourself");
            await client.Closed.Task;
            await propagationEntered.Task;
            disposing = factory.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, resource.Disposals);
            client.Record("provider-release");
            client.Release.TrySetResult();
            await client.RegistrationDisposed.Task;
            Assert.Equal(0, client.CallbackInvocations);
            Assert.False(disposing.IsCompleted);
            releasePropagation.Set();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposing);
            var originals = failure.Flatten().InnerExceptions;
            WriteOwnershipTrace(client, resource, originals);
            Assert.Equal(2, originals.Count);
            Assert.Single(originals, error => ReferenceEquals(error, client.Failure));
            Assert.Single(originals, error => ReferenceEquals(error, resource.Failure));
            Assert.DoesNotContain(originals, error => ReferenceEquals(error, client.CancellationFailure));
            Assert.Equal(0, client.CallbackInvocations);
            Assert.Equal(1, resource.Disposals);
        }
        finally
        {
            releasePropagation.Set();
            client.Release.TrySetResult();
            propagationGate.Dispose();
            if (disposing is not null)
                await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private void WriteOwnershipTrace(
        SelfTerminatingClient client, FailureResource resource, IReadOnlyCollection<Exception> originals)
        => output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            client.Events,
            client.CallbackInvocations,
            CancellationErrors = originals.Count(error => ReferenceEquals(error, client.CancellationFailure)),
            ProviderErrors = originals.Count(error => ReferenceEquals(error, client.Failure)),
            ResourceErrors = originals.Count(error => ReferenceEquals(error, resource.Failure)),
            OtherErrors = originals.Count(error => !ReferenceEquals(error, client.CancellationFailure)
                && !ReferenceEquals(error, client.Failure) && !ReferenceEquals(error, resource.Failure)),
            resource.Disposals,
        }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FactorySelfTermination_ReturnsToProviderButExternalOwnersAwaitCleanup(
        bool providerFails, bool resourceFails)
    {
        var client = new SelfTerminatingClient(providerFails);
        var resource = new FailureResource(new IOException("independent disposal failure"));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(),
            new AgentServices { ChatClientOverride = client }, scheduler.ExclusiveScheduler);
        var sessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId(Guid.NewGuid().ToString());
        var lease = await factory.GetOrCreateAsync(sessionId,
            AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson));
        if (resourceFails) lease.LocalAgentChat.RegisterOwnedResource(resource);
        client.Close = async () =>
        {
            Assert.True(await factory.TerminateExactAsync(sessionId, lease.AgentChat));
            return await factory.TerminateExactAsync(sessionId, lease.AgentChat);
        };
        lease.LocalAgentChat.EnqueueUserMessage("close yourself");
        await client.Closed.Task;

        Task? terminating = null;
        Task? disposing = null;
        try
        {
            Assert.Empty(factory.RunningSessions);
            Assert.False(lease.IsRuntimeTerminal);
            terminating = factory.TerminateExactAsync(sessionId, lease.AgentChat);
            disposing = factory.DisposeAsync().AsTask();
            Assert.False(terminating.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, resource.Disposals);

            client.Release.TrySetResult();
            if (providerFails || resourceFails)
            {
                var terminationFailure = await Assert.ThrowsAsync<AggregateException>(() => terminating);
                var disposalFailure = await Assert.ThrowsAsync<AggregateException>(() => disposing);
                Assert.Same(terminationFailure, disposalFailure);
                var originals = terminationFailure.Flatten().InnerExceptions;
                Assert.Equal((providerFails ? 1 : 0) + (resourceFails ? 1 : 0), originals.Count);
                if (providerFails) Assert.Single(originals, error => ReferenceEquals(error, client.Failure));
                if (resourceFails) Assert.Single(originals, error => ReferenceEquals(error, resource.Failure));
            }
            else
            {
                await terminating;
                await disposing;
                Assert.True(lease.IsRuntimeTerminal);
            }
            Assert.Equal(resourceFails ? 1 : 0, resource.Disposals);
        }
        finally
        {
            client.Release.TrySetResult();
            if (terminating is not null)
                await terminating.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (disposing is not null)
                await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class SelfTerminatingClient(bool fails, bool cancellationFails = false) : IChatClient
    {
        internal Func<Task<bool>>? Close { get; set; }
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RegistrationDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnRegistered { get; set; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> events = new();
        private int callbackInvocations;
        internal int CallbackInvocations => Volatile.Read(ref this.callbackInvocations);
        internal string[] Events => this.events.ToArray();
        internal void Record(string value) => this.events.Enqueue(value);
        internal Exception Failure { get; } = new IOException("late provider failure");
        internal Exception CancellationFailure { get; } = new InvalidOperationException("cancellation callback failure");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var registration = cancellationFails
                ? cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref this.callbackInvocations);
                    this.Record("cancellation-callback-invoked");
                    this.CancellationInvoked.TrySetResult();
                    throw this.CancellationFailure;
                })
                : default;
            this.Record("provider-registered");
            this.OnRegistered?.Invoke();
            try
            {
                Assert.True(await this.Close!());
                this.Record("self-close-returned");
                this.Closed.TrySetResult();
                await this.Release.Task;
                if (fails)
                {
                    this.Record("provider-failure");
                    throw this.Failure;
                }
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
            finally
            {
                this.Record("enumerator-unwind");
                registration.Dispose();
                this.Record("registration-disposed");
                this.RegistrationDisposed.TrySetResult();
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_RetainedTerminalAndIndependentCleanupFailures_ReportEachOriginalOnce(bool independentCleanupFails)
    {
        var terminal = new IOException("native request failed");
        var independent = new IOException("native request failed");
        var native = new FakeCopilotSession
        {
            SendHandler = (_, _) => Task.FromException(terminal),
        };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var resource = new FailureResource(independentCleanupFails
            ? new AggregateException(terminal, independent)
            : terminal);
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new AgentServices
            {
                RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory(),
            },
            OwnedResources = [resource],
        });

        var completion = chat.EnqueueUserMessageWithCompletion("request");
        Assert.Same(terminal, await Assert.ThrowsAsync<IOException>(() => completion.Settlement));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => chat.DisposeAsync().AsTask());

        Assert.Equal<Exception>(
            independentCleanupFails ? [terminal, independent] : [terminal],
            failure.Flatten().InnerExceptions);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(0, native.SubscriberCount);
        Assert.Equal(1, native.SendAsyncCallCount);
        await chat.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
    }

    private sealed class FailureResource(Exception failure, Action<string>? observe = null) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public Exception Failure => failure;

        public ValueTask DisposeAsync()
        {
            observe?.Invoke("resource-disposal");
            this.Disposals++;
            return ValueTask.FromException(failure);
        }
    }

    [Fact]
    public async Task AgentChat_Dispose_DoesNotHang()
    {
        // The process loop is blocked on queueStateSignal.WaitAsync waiting for input.
        // Disposal must cancel that wait so DisposeAsync returns promptly.
        var client = new DeterministicTestChatClient();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            AgentServices = new AgentServices { ChatClientOverride = client },
        });

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task AgentChat_Dispose_WithFrozenDomainTime_CancelsWhenDisposalTimeoutExpires()
    {
        var client = new DeterministicTestChatClient();
        var frozenDomainTime = new FakeTimeProvider();
        var disposalTime = new FakeTimeProvider();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agent,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            TimeProvider = frozenDomainTime,
            DisposalTimeProvider = disposalTime,
        });

        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync();

        var disposal = chat.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        disposalTime.Advance(AgentChat.DisposeDrainTimeout);

        await disposal;
    }

    [Fact]
    public void InternalCreateAgentChatRequest_DisposalTimeProvider_DefaultsToSystemTime()
    {
        var request = new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
        };

        Assert.Same(TimeProvider.System, request.DisposalTimeProvider);
    }

    [Fact]
    public async Task AgentChat_HostedDispose_WithFrozenDomainTime_CancelsWhenDisposalTimeoutExpires()
    {
        var client = new BlockingHostedChatClient();
        var frozenDomainTime = new FakeTimeProvider();
        var disposalTime = new FakeTimeProvider();
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            TimeProvider = frozenDomainTime,
            DisposalTimeProvider = disposalTime,
        });
        await client.StreamingStarted;

        var disposal = chat.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        disposalTime.Advance(AgentChat.DisposeDrainTimeout);
        await disposal;

        Assert.True(client.CancellationObserved);
    }


    private const string EchoAgentJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    [Fact]
    public async Task CreateAgentChat_DisposesOwnedAsyncDisposableChatClient()
    {
        var trackingClient = new DisposalTrackingChatClient();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            AgentServices = new AgentServices { ChatClientOverride = trackingClient },
        });

        Assert.False(trackingClient.Disposed);

        await chat.DisposeAsync();

        Assert.True(trackingClient.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_OwnedResourceFailure_DisposesRemainingProductionResourcesAndReportsFailure()
    {
        var first = new ThrowingResource();
        var second = new TrackingResource();
        var childResource = new TrackingResource();
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            OwnedResources = [first, second],
        });
        await chat.GetOrCreateAsync(
            "child",
            AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            "tool-call");
        var child = Assert.IsType<AgentChat>(Assert.Single(chat.SubAgents));
        child.RegisterOwnedResource(childResource);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => chat.DisposeAsync().AsTask());

        Assert.True(first.DisposeAttempted);
        Assert.True(second.Disposed);
        Assert.True(childResource.Disposed);
        Assert.Contains(failure.InnerExceptions,
            error => error.Message == "owned resource failed");
    }

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

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingHostedChatClient : IChatClient, IHostedAgentChatClient
    {
        private readonly TaskCompletionSource streamingStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StreamingStarted => this.streamingStarted.Task;

        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.streamingStarted.SetResult();
            try
            {
                await never.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                this.CancellationObserved = cancellationToken.IsCancellationRequested;
            }

            yield break;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingResource : IAsyncDisposable
    {
        internal bool DisposeAttempted { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeAttempted = true;
            return ValueTask.FromException(new InvalidOperationException("owned resource failed"));
        }
    }

    private sealed class TrackingResource : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

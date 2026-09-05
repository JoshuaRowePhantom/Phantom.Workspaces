using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Issue #1443 (per-component-executor-binding, Commit 6B): a <c>model.options.executor</c> binding
/// routes ONLY the innermost Copilot SDK session over a transport, while the router and context
/// providers stay in-process. These tests exercise the routing decision (via the
/// <c>ResolveRemoteClientForTestAsync</c> seam) without spinning up a live streaming turn.
/// </summary>
public sealed class CopilotSdkChatClientExecutorTests
{
    [Fact]
    public async Task ModelOptionsExecutor_UnsetOrLocal_UsesInProcessSession()
    {
        // No bindings / registry at all -> behaviour-preserving in-process session.
        var noRouting = ExecutorRoutingTestHarness.CreateClient("model-host");
        Assert.Null(await noRouting.ResolveRemoteClientForTestAsync());

        // Executor option unset -> inherits the (default local) session executor -> in-process.
        var (transport1, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var _t1 = transport1;
        var registry1 = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport1);
        var unset = ExecutorRoutingTestHarness.CreateClient(executorName: null);
        unset.ConfigureExecutorRouting(new ExecutorBindings(), registry1);
        Assert.Null(await unset.ResolveRemoteClientForTestAsync());
        Assert.Equal(0, registry1.ConnectCount);

        // Executor bound to an explicit local descriptor -> in-process.
        var (transport2, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var _t2 = transport2;
        var registry2 = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport2);
        var local = ExecutorRoutingTestHarness.CreateClient("model-host");
        local.ConfigureExecutorRouting(ExecutorRoutingTestHarness.LocalModelBindings("model-host"), registry2);
        Assert.Null(await local.ResolveRemoteClientForTestAsync());
        Assert.Equal(0, registry2.ConnectCount);
    }

    [Fact]
    public async Task ModelOptionsExecutor_NamedRemote_ResolvesViaSharedResolver()
    {
        var (transport, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var _t = transport;
        var registry = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport);
        var client = ExecutorRoutingTestHarness.CreateClient("model-host");
        client.ConfigureExecutorRouting(ExecutorRoutingTestHarness.RemoteModelBindings("model-host"), registry);

        var remote = await client.ResolveRemoteClientForTestAsync();

        Assert.NotNull(remote);
        Assert.Equal(1, registry.ConnectCount);
        Assert.True(registry.LastDescriptor.HasValue);

        // The descriptor recorded under the model is exactly the one produced by the shared
        // ExecutorBindings.ResolveComponent resolver for the named executor.
        var descriptor = registry.LastDescriptor!.Value;
        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ExecutorRoutingTestHarness.RemoteEntityId, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task ModelOptionsExecutor_RemoteDescriptor_CreatesSessionOverTransport()
    {
        var (transport, factory) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var _t = transport;
        var registry = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport);
        var client = ExecutorRoutingTestHarness.CreateClient("model-host");
        client.ConfigureExecutorRouting(ExecutorRoutingTestHarness.RemoteModelBindings("model-host"), registry);

        var remote = await client.ResolveRemoteClientForTestAsync();
        Assert.NotNull(remote);
        Assert.IsType<CopilotClientOverTransport>(remote);

        await using var session = await remote!.CreateSessionAsync(
            new SessionConfig { Model = "gpt-5" },
            ExecutorRoutingTestHarness.Ct());

        // The create/resume went through the transport factory and the remote host's local CLI factory,
        // NOT the caller's in-process DefaultCopilotClientFactory.
        Assert.Equal(1, registry.ConnectCount);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.Client.CreateSessionCount);
        Assert.Equal("remote-session-42", session.SessionId);

        // The scalar model field survived the wire and was applied to the rebuilt host-side config.
        Assert.Equal("gpt-5", factory.Client.LastConfig!.Model);
    }
}

/// <summary>Shared fakes / helpers for the issue #1443 executor-binding routing tests.</summary>
internal static class ExecutorRoutingTestHarness
{
    public const string RemoteEntityId = "22222222-2222-2222-2222-222222222222";

    public static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    public static CopilotSdkChatClient CreateClient(string? executorName)
    {
        var options = new ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>(),
        };
        if (executorName is not null)
        {
            options.AdditionalProperties["executor"] = executorName;
        }

        return new CopilotSdkChatClient(
            modelId: "gpt-5",
            displayName: "GPT-5",
            gitHubToken: null,
            loggerFactory: null,
            modelOptions: options);
    }

    public static ExecutorBindings RemoteModelBindings(string name)
    {
        using var document = JsonDocument.Parse(
            $$"""{"type":"user-computer-profile","entity-id":"{{RemoteEntityId}}"}""");
        return new ExecutorBindings
        {
            Bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [name] = document.RootElement.Clone(),
            },
        };
    }

    public static ExecutorBindings LocalModelBindings(string name)
        => new ExecutorBindings
        {
            Bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [name] = ExecutorBindings.LocalDescriptor(),
            },
        };

    public static (LocalTransport Transport, RecordingClientFactory Factory) BuildHostTransport()
    {
        var factory = new RecordingClientFactory();
        var registry = new TransportRegistry();
        registry.Register(new CopilotClientTransportListener(factory));
        return (new LocalTransport(registry), factory);
    }

    internal sealed class RecordingTransportFactoryRegistry : ITransportFactoryRegistry
    {
        private readonly ITransport transport;

        public RecordingTransportFactoryRegistry(ITransport transport) => this.transport = transport;

        public JsonElement? LastDescriptor { get; private set; }

        public int ConnectCount { get; private set; }

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.LastDescriptor = connectionDescriptor.Clone();
            this.ConnectCount++;
            return Task.FromResult(this.transport);
        }
    }

    internal sealed class RecordingClientFactory : ICopilotClientFactory
    {
        public int CreateCount { get; private set; }

        public RecordingCopilotClient Client { get; } = new();

        public ICopilotClient Create(CopilotClientOptions options)
        {
            this.CreateCount++;
            return this.Client;
        }
    }

    internal sealed class RecordingCopilotClient : ICopilotClient
    {
        public int CreateSessionCount { get; private set; }

        public SessionConfig? LastConfig { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());

        public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            this.CreateSessionCount++;
            this.LastConfig = config;
            return Task.FromResult<ICopilotSession>(new StubCopilotSession("remote-session-42"));
        }

        public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            => Task.FromResult<ICopilotSession>(new StubCopilotSession(sessionId));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class StubCopilotSession : ICopilotSession
    {
        public StubCopilotSession(string sessionId) => this.SessionId = sessionId;

        public string SessionId { get; }

        public IDisposable Subscribe(Action<SessionEvent> handler) => new NoopDisposable();

        public Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken)
            => Task.FromResult<AssistantMessageEvent?>(null);

        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

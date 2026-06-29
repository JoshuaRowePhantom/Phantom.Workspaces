using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TrustedExecutorCompositionTests
{
    [Fact]
    public void CreateSelector_RoutesLocalToLocal_AndConnectedRemoteToReverse()
    {
        var registry = new ReverseExecutionRegistry();
        var selector = TrustedExecutorComposition.CreateSelector(registry);
        var profile = new TrustProfile { HostingWorkspacesClientInstances = ["*"] };

        // The local instance is served by the local executor.
        Assert.IsType<LocalTrustedExecutor>(selector.SelectExecutor(profile, TrustProfile.LocalClientInstance));

        // A connected remote instance is served by the registry-backed reverse executor.
        registry.Register(new FakeReverseConnection("remote-1"));
        Assert.IsType<ReverseTrustedExecutor>(selector.SelectExecutor(profile, "remote-1"));
    }

    [Fact]
    public void CreateSelector_DisconnectedRemote_HasNoExecutor()
    {
        var registry = new ReverseExecutionRegistry();
        var selector = TrustedExecutorComposition.CreateSelector(registry);
        var profile = new TrustProfile { HostingWorkspacesClientInstances = ["*"] };

        Assert.Throws<InvalidOperationException>(() => selector.SelectExecutor(profile, "not-connected"));
    }

    [Fact]
    public void CreateSelector_WithRemoteExecutor_RoutesRegisteredRemoteToRemoteExecutor()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var fakeRemote = new FakeRemoteExecutor("outbound-host");
        var selector = TrustedExecutorComposition.CreateSelector(reverseRegistry, fakeRemote);
        var profile = new TrustProfile { HostingWorkspacesClientInstances = ["*"] };

        // When the reverse registry has no connection for the instance but the remote executor can
        // serve it, the selector picks the remote executor.
        Assert.Same(fakeRemote, selector.SelectExecutor(profile, "outbound-host"));
    }

    [Fact]
    public void CreateSelector_WithRemoteExecutor_PrefersReverseOverRemote_WhenBothAvailable()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var fakeRemote = new FakeRemoteExecutor("shared-host");
        var selector = TrustedExecutorComposition.CreateSelector(reverseRegistry, fakeRemote);
        var profile = new TrustProfile { HostingWorkspacesClientInstances = ["*"] };

        // Connecting the instance via the reverse WebSocket makes ReverseTrustedExecutor available.
        reverseRegistry.Register(new FakeReverseConnection("shared-host"));

        // Reverse is earlier in the selector, so it wins.
        Assert.IsType<ReverseTrustedExecutor>(selector.SelectExecutor(profile, "shared-host"));
    }

    [Fact]
    public void CreateSelector_WithRemoteExecutor_LocalStillServedByLocalExecutor()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var fakeRemote = new FakeRemoteExecutor("remote-a");
        var selector = TrustedExecutorComposition.CreateSelector(reverseRegistry, fakeRemote);
        var profile = new TrustProfile { HostingWorkspacesClientInstances = ["*"] };

        Assert.IsType<LocalTrustedExecutor>(selector.SelectExecutor(profile, TrustProfile.LocalClientInstance));
    }

    private sealed class FakeReverseConnection : IReverseConnection
    {
        public FakeReverseConnection(string clientInstanceId)
        {
            this.ClientInstanceId = clientInstanceId;
        }

        public string ClientInstanceId { get; }

        public string? AnnouncedEndpoint => null;

        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

        public int InFlightCount => 0;

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            yield break;
        }

        public Task<System.IO.Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeRemoteExecutor : ITrustedExecutor
    {
        private readonly string targetInstance;

        public FakeRemoteExecutor(string targetInstance)
        {
            this.targetInstance = targetInstance;
        }

        public bool CanExecute(string targetClientInstance)
            => string.Equals(targetClientInstance, this.targetInstance, StringComparison.Ordinal);

        public Task<AgentChat> CreateAgentChatAsync(
            TrustedExecutionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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

    private sealed class FakeReverseConnection : IReverseConnection
    {
        public FakeReverseConnection(string clientInstanceId)
        {
            this.ClientInstanceId = clientInstanceId;
        }

        public string ClientInstanceId { get; }

        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

        public int InFlightCount => 0;

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            yield break;
        }
    }
}

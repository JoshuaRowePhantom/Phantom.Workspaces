using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Trust;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class DynamicRemoteTrustedExecutorTests
{
    [Fact]
    public void CanExecute_ReturnsTrueForRegisteredInstance()
    {
        var registry = new RemoteExecutionRegistry();
        registry.Register("computer-a", "https://computer-a.example/");
        var executor = new DynamicRemoteTrustedExecutor(registry);

        Assert.True(executor.CanExecute("computer-a"));
    }

    [Fact]
    public void CanExecute_ReturnsFalseForUnregisteredInstance()
    {
        var registry = new RemoteExecutionRegistry();
        var executor = new DynamicRemoteTrustedExecutor(registry);

        Assert.False(executor.CanExecute("computer-a"));
    }

    [Fact]
    public void CanExecute_ReturnsFalseForLocalInstance()
    {
        var registry = new RemoteExecutionRegistry();
        // Even if somehow the local instance were registered, it must not be routed remotely.
        var executor = new DynamicRemoteTrustedExecutor(registry);

        Assert.False(executor.CanExecute(TrustProfile.LocalClientInstance));
    }

    [Fact]
    public async Task CreateAgentChat_Throws_WhenNotRegistered()
    {
        var registry = new RemoteExecutionRegistry();
        var executor = new DynamicRemoteTrustedExecutor(registry);
        var request = new TrustedExecutionRequest
        {
            AgentDefinition = AgentSchema.AgentDefinition.FromJson("""{ "kind": "prompt", "name": "x" }"""),
            TrustProfile = new TrustProfile { HostingWorkspacesClientInstances = ["computer-a"] },
            TargetClientInstance = "computer-a",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.CreateAgentChatAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenStreamAsync_ThrowsNotImplemented()
    {
        var registry = new RemoteExecutionRegistry();
        var executor = new DynamicRemoteTrustedExecutor(registry);
        var request = new TrustedStreamRequest
        {
            TargetClientInstance = "computer-a",
            StreamKind = "shell",
            OpenPayload = JsonDocument.Parse("{}").RootElement,
        };

        await Assert.ThrowsAsync<NotImplementedException>(() => executor.OpenStreamAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DynamicRemoteTrustedExecutor(null!));
    }
}

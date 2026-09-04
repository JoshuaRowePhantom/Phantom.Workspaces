using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Trust;

namespace Phantom.Workspaces.Tests;

public sealed class DeferredTrustedExecutorSelectorTests
{
    private sealed class SpyTrustedExecutor : ITrustedExecutor
    {
        private readonly string clientInstance;

        public SpyTrustedExecutor(string clientInstance)
        {
            this.clientInstance = clientInstance;
        }

        public bool CanExecute(string targetClientInstance)
            => string.Equals(this.clientInstance, targetClientInstance, StringComparison.Ordinal);

        public Task<AgentChat> CreateAgentChatAsync(
            TrustedExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public void SelectExecutorForTarget_RouterLocalTopology_GuiLocalCallDispatchesToLocalExecutor()
    {
        var selector = new DeferredTrustedExecutorSelector();
        var remoteExecutor = new SpyTrustedExecutor("remote-instance");
        selector.SetRemoteExecutor(remoteExecutor);

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = TrustProfile.LocalClientInstance,
            HostingInstanceClientInstance = TrustProfile.LocalClientInstance,
            AgentExecutorClientInstance = "remote-instance"
        };
        selector.SetTopology(topology);

        var result = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);

        Assert.IsType<LocalTrustedExecutor>(result);
    }

    [Fact]
    public void SelectExecutorForTarget_RouterLocalTopology_NonGuiLocalCallDispatchesToTransportExecutor()
    {
        var selector = new DeferredTrustedExecutorSelector();
        var remoteExecutor = new SpyTrustedExecutor("remote-instance");
        selector.SetRemoteExecutor(remoteExecutor);

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = TrustProfile.LocalClientInstance,
            HostingInstanceClientInstance = TrustProfile.LocalClientInstance,
            AgentExecutorClientInstance = "remote-instance"
        };
        selector.SetTopology(topology);

        var result = selector.SelectExecutorForTarget(ExecutorTarget.AgentExecutor);

        Assert.Same(remoteExecutor, result);
    }

    [Fact]
    public void SelectExecutorForTarget_FullExecutorRemoteTopology_BehavesAsToday()
    {
        var selector = new DeferredTrustedExecutorSelector();
        var remoteExecutor = new SpyTrustedExecutor("remote-instance");
        selector.SetRemoteExecutor(remoteExecutor);

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = "remote-instance",
            HostingInstanceClientInstance = "remote-instance",
            AgentExecutorClientInstance = "remote-instance"
        };
        selector.SetTopology(topology);

        var resultGuiLocal = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);
        var resultAgentExecutor = selector.SelectExecutorForTarget(ExecutorTarget.AgentExecutor);
        var resultHostingInstance = selector.SelectExecutorForTarget(ExecutorTarget.HostingInstance);

        Assert.Same(remoteExecutor, resultGuiLocal);
        Assert.Same(remoteExecutor, resultAgentExecutor);
        Assert.Same(remoteExecutor, resultHostingInstance);
    }

    [Fact]
    public void SelectExecutorForTarget_SingleMachineTopology_AllTargetsDispatchToLocal()
    {
        var selector = new DeferredTrustedExecutorSelector();
        selector.SetTopology(ExecutorTopology.SingleMachine);

        var resultGuiLocal = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);
        var resultAgentExecutor = selector.SelectExecutorForTarget(ExecutorTarget.AgentExecutor);
        var resultHostingInstance = selector.SelectExecutorForTarget(ExecutorTarget.HostingInstance);

        Assert.IsType<LocalTrustedExecutor>(resultGuiLocal);
        Assert.IsType<LocalTrustedExecutor>(resultAgentExecutor);
        Assert.IsType<LocalTrustedExecutor>(resultHostingInstance);
    }

    [Fact]
    public void SelectExecutorForTarget_NoTopologySet_DefaultsToSingleMachine()
    {
        var selector = new DeferredTrustedExecutorSelector();

        var resultGuiLocal = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);
        var resultAgentExecutor = selector.SelectExecutorForTarget(ExecutorTarget.AgentExecutor);
        var resultHostingInstance = selector.SelectExecutorForTarget(ExecutorTarget.HostingInstance);

        Assert.IsType<LocalTrustedExecutor>(resultGuiLocal);
        Assert.IsType<LocalTrustedExecutor>(resultAgentExecutor);
        Assert.IsType<LocalTrustedExecutor>(resultHostingInstance);
    }

    [Fact]
    public void SelectExecutorForTarget_WorkspaceGuiAndWorkspaceEntityTools_AlwaysLocalUnderRouterLocalTopology()
    {
        var selector = new DeferredTrustedExecutorSelector();
        var remoteExecutor = new SpyTrustedExecutor("remote-instance");
        selector.SetRemoteExecutor(remoteExecutor);

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = TrustProfile.LocalClientInstance,
            HostingInstanceClientInstance = TrustProfile.LocalClientInstance,
            AgentExecutorClientInstance = "remote-instance"
        };
        selector.SetTopology(topology);

        // workspace-gui and workspace-entity are classified as GuiLocal
        var result = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);

        Assert.IsType<LocalTrustedExecutor>(result);
    }

    [Fact]
    public void SelectExecutorForTarget_NoRemoteExecutorAvailable_ThrowsForRemoteTarget()
    {
        var selector = new DeferredTrustedExecutorSelector();

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = TrustProfile.LocalClientInstance,
            HostingInstanceClientInstance = TrustProfile.LocalClientInstance,
            AgentExecutorClientInstance = "remote-instance"
        };
        selector.SetTopology(topology);

        Assert.Throws<InvalidOperationException>(() =>
            selector.SelectExecutorForTarget(ExecutorTarget.AgentExecutor));
    }

    [Fact]
    public void SetTopology_NullTopology_DefaultsToSingleMachine()
    {
        var selector = new DeferredTrustedExecutorSelector();
        selector.SetTopology(null);

        var resultGuiLocal = selector.SelectExecutorForTarget(ExecutorTarget.GuiLocal);

        Assert.IsType<LocalTrustedExecutor>(resultGuiLocal);
    }

    [Fact]
    public void SelectExecutor_BackwardCompatibility_StillWorks()
    {
        var selector = new DeferredTrustedExecutorSelector();
        var trustProfile = new TrustProfile
        {
            HostingWorkspacesClientInstances = [TrustProfile.LocalClientInstance]
        };

        var result = selector.SelectExecutor(trustProfile, TrustProfile.LocalClientInstance);

        Assert.IsType<LocalTrustedExecutor>(result);
    }
}

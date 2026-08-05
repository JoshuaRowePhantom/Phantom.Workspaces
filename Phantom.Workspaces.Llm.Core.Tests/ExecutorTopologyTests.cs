using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ExecutorTopologyTests
{
    [Fact]
    public void SingleMachine_AllTargets_ResolveLocal()
    {
        var topology = ExecutorTopology.SingleMachine;

        Assert.Equal(".", topology.Resolve(ExecutorTarget.AgentExecutor));
        Assert.Equal(".", topology.Resolve(ExecutorTarget.GuiLocal));
        Assert.Equal(".", topology.Resolve(ExecutorTarget.HostingInstance));
    }

    [Fact]
    public void SingleMachine_AllTargets_ResolveLocallyWithNoRoundTrip()
    {
        var topology = ExecutorTopology.SingleMachine;

        Assert.True(topology.ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.True(topology.ResolvesLocally(ExecutorTarget.GuiLocal));
        Assert.True(topology.ResolvesLocally(ExecutorTarget.HostingInstance));
        Assert.True(topology.IsSingleMachine);
    }

    [Fact]
    public void Resolve_MultiMachineTopology_ReturnsPerTargetInstances()
    {
        var topology = new ExecutorTopology
        {
            AgentExecutorClientInstance = "E",
            GuiLocalClientInstance = "G",
            HostingInstanceClientInstance = "H",
        };

        Assert.Equal("E", topology.Resolve(ExecutorTarget.AgentExecutor));
        Assert.Equal("G", topology.Resolve(ExecutorTarget.GuiLocal));
        Assert.Equal("H", topology.Resolve(ExecutorTarget.HostingInstance));
    }

    [Fact]
    public void IsSingleMachine_DistinctInstances_IsFalse()
    {
        var topology = new ExecutorTopology
        {
            AgentExecutorClientInstance = "E",
            GuiLocalClientInstance = "G",
            HostingInstanceClientInstance = "H",
        };

        Assert.False(topology.IsSingleMachine);
        Assert.False(topology.ResolvesLocally(ExecutorTarget.AgentExecutor));
    }

    [Fact]
    public void IsSingleMachine_SameNonLocalInstance_IsTrueButNotLocal()
    {
        var topology = new ExecutorTopology
        {
            AgentExecutorClientInstance = "E",
            GuiLocalClientInstance = "E",
            HostingInstanceClientInstance = "E",
        };

        Assert.True(topology.IsSingleMachine);
        Assert.False(topology.ResolvesLocally(ExecutorTarget.GuiLocal));
    }
}

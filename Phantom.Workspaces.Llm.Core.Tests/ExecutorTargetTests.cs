using AgentSchema;
using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ExecutorTargetTests
{
    [Theory]
    [InlineData("mcp")]
    [InlineData("function")]
    [InlineData("web")]
    [InlineData("filesystem")]
    [InlineData("")]
    [InlineData(null)]
    public void ForKind_DefaultAndExecutorKinds_ReturnsAgentExecutor(string? kind)
    {
        Assert.Equal(ExecutorTarget.AgentExecutor, ExecutorTargetResolver.ForKind(kind));
    }

    [Theory]
    [InlineData("workspace-gui")]
    [InlineData("workspace-entity")]
    [InlineData("WORKSPACE-GUI")]
    public void ForKind_WorkspaceGuiAndEntity_ReturnsGuiLocal(string kind)
    {
        Assert.Equal(ExecutorTarget.GuiLocal, ExecutorTargetResolver.ForKind(kind));
    }

    [Theory]
    [InlineData("agent-session")]
    [InlineData("workspace-agent-session")]
    [InlineData("Agent-Session")]
    public void ForKind_AgentSessionKinds_ReturnsHostingInstance(string kind)
    {
        Assert.Equal(ExecutorTarget.HostingInstance, ExecutorTargetResolver.ForKind(kind));
    }

    [Fact]
    public void ForTool_McpTool_IsAgentExecutor()
    {
        var tool = new McpTool { ServerName = "srv" };

        Assert.Equal("mcp", tool.Kind);
        Assert.Equal(ExecutorTarget.AgentExecutor, ExecutorTargetResolver.ForTool(tool));
    }

    [Fact]
    public void ForTool_WorkspaceGuiTool_IsGuiLocal()
    {
        var tool = new CustomTool { Kind = "workspace-gui", Name = "gui" };

        Assert.Equal(ExecutorTarget.GuiLocal, ExecutorTargetResolver.ForTool(tool));
    }

    [Fact]
    public void ForTool_WorkspaceEntityTool_IsGuiLocal()
    {
        var tool = new CustomTool { Kind = "workspace-entity", Name = "entity" };

        Assert.Equal(ExecutorTarget.GuiLocal, ExecutorTargetResolver.ForTool(tool));
    }

    [Fact]
    public void ForTool_AgentSessionTool_IsHostingInstance()
    {
        var tool = new CustomTool { Kind = "agent-session", Name = "session" };

        Assert.Equal(ExecutorTarget.HostingInstance, ExecutorTargetResolver.ForTool(tool));
    }

    [Fact]
    public void ForTool_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExecutorTargetResolver.ForTool(null!));
    }
}

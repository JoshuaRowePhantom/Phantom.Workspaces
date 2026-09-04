using AgentSchema;
using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ExecutorTargetResolverTopologyTests
{
    [Fact]
    public void ForKindWithTargetSession_AgentSessionToolTargetingSourceSession_ClassifiesAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-abc";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "agent-session",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.GuiLocal, result);
    }

    [Fact]
    public void ForKindWithTargetSession_WorkspaceAgentSessionToolTargetingSourceSession_ClassifiesAsGuiLocal()
    {
        const string sourceSessionId = "session-xyz";
        const string targetSessionId = "session-xyz";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "workspace-agent-session",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.GuiLocal, result);
    }

    [Fact]
    public void ForKindWithTargetSession_AgentSessionToolTargetingOtherSession_DoesNotClassifyAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-xyz";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "agent-session",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.HostingInstance, result);
    }

    [Fact]
    public void ForKindWithTargetSession_AgentSessionToolWithNullSourceSession_DoesNotClassifyAsGuiLocal()
    {
        const string targetSessionId = "session-xyz";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "agent-session",
            null,
            targetSessionId);

        Assert.Equal(ExecutorTarget.HostingInstance, result);
    }

    [Fact]
    public void ForKindWithTargetSession_AgentSessionToolWithNullTargetSession_DoesNotClassifyAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "agent-session",
            sourceSessionId,
            null);

        Assert.Equal(ExecutorTarget.HostingInstance, result);
    }

    [Fact]
    public void ForKindWithTargetSession_WorkspaceGuiKind_AlwaysClassifiesAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-xyz";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "workspace-gui",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.GuiLocal, result);
    }

    [Fact]
    public void ForKindWithTargetSession_WorkspaceEntityKind_AlwaysClassifiesAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-xyz";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "workspace-entity",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.GuiLocal, result);
    }

    [Fact]
    public void ForKindWithTargetSession_McpKind_AlwaysClassifiesAsAgentExecutor()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-abc";

        var result = ExecutorTargetResolver.ForKindWithTargetSession(
            "mcp",
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.AgentExecutor, result);
    }

    [Fact]
    public void ForToolWithTargetSession_AgentSessionToolTargetingSourceSession_ClassifiesAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-abc";
        var tool = new CustomTool { Kind = "agent-session", Name = "session" };

        var result = ExecutorTargetResolver.ForToolWithTargetSession(
            tool,
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.GuiLocal, result);
    }

    [Fact]
    public void ForToolWithTargetSession_AgentSessionToolTargetingOtherSession_DoesNotClassifyAsGuiLocal()
    {
        const string sourceSessionId = "session-abc";
        const string targetSessionId = "session-xyz";
        var tool = new CustomTool { Kind = "agent-session", Name = "session" };

        var result = ExecutorTargetResolver.ForToolWithTargetSession(
            tool,
            sourceSessionId,
            targetSessionId);

        Assert.Equal(ExecutorTarget.HostingInstance, result);
    }

    [Fact]
    public void ForToolWithTargetSession_NullTool_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExecutorTargetResolver.ForToolWithTargetSession(null!, "session-abc", "session-xyz"));
    }
}

using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class SubAgentEntityNamingTests
{
    [Fact]
    public void AppendSubAgentId_AppendsSlugAsTerminalComponent()
    {
        var dispatcherName = new EntityName("users", "username", "alice", "agent-sessions", "my-dispatcher");

        var subAgentName = SubAgentEntityNaming.AppendSubAgentId(dispatcherName, "foo-bar");

        Assert.Equal(
            new[] { "users", "username", "alice", "agent-sessions", "my-dispatcher", "foo-bar" },
            subAgentName.Components);
    }

    [Fact]
    public void AppendSubAgentId_DoesNotMutateDispatcherName()
    {
        var dispatcherName = new EntityName("users", "username", "alice", "agent-sessions", "my-dispatcher");

        _ = SubAgentEntityNaming.AppendSubAgentId(dispatcherName, "foo-bar");

        Assert.Equal(
            new[] { "users", "username", "alice", "agent-sessions", "my-dispatcher" },
            dispatcherName.Components);
    }

    [Fact]
    public void ExpandSubAgentNames_ProducesBothUsernameAndIdPrefixedForms()
    {
        var dispatcherNames = new[]
        {
            new EntityName("users", "username", "alice", "agent-sessions", "my-dispatcher"),
            new EntityName("users", "id", "user-1234", "agent-sessions", "my-dispatcher"),
        };

        var subAgentNames = SubAgentEntityNaming.ExpandSubAgentNames(dispatcherNames, "foo-bar");

        Assert.Equal(2, subAgentNames.Count);
        Assert.Equal(
            new[] { "users", "username", "alice", "agent-sessions", "my-dispatcher", "foo-bar" },
            subAgentNames[0].Components);
        Assert.Equal(
            new[] { "users", "id", "user-1234", "agent-sessions", "my-dispatcher", "foo-bar" },
            subAgentNames[1].Components);
    }

    [Fact]
    public void AppendSubAgentId_EmptyId_Throws()
    {
        var dispatcherName = new EntityName("users", "username", "alice", "agent-sessions", "my-dispatcher");

        Assert.Throws<ArgumentException>(() => SubAgentEntityNaming.AppendSubAgentId(dispatcherName, ""));
    }
}

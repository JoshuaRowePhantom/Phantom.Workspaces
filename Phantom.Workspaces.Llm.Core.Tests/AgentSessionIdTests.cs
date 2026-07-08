using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentSessionIdTests
{
    [Fact]
    public void AgentSessionId_Equality_SameValue_Equal()
    {
        var a = new AgentSessionId("session-1");
        var b = new AgentSessionId("session-1");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void AgentSessionId_Equality_DifferentValue_NotEqual()
    {
        var a = new AgentSessionId("session-1");
        var b = new AgentSessionId("session-2");

        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void AgentSessionId_ToString_ReturnsValue()
    {
        var id = new AgentSessionId("my-session");

        Assert.Equal("my-session", id.ToString());
    }

    [Fact]
    public void AgentSessionId_DefaultValue_EmptyString()
    {
        var id = default(AgentSessionId);

        Assert.Null(id.Value);
    }

    [Fact]
    public void AgentSessionId_UsedAsDictionaryKey_WorksCorrectly()
    {
        var dict = new Dictionary<AgentSessionId, int>();
        var key = new AgentSessionId("session-key");

        dict[key] = 42;

        Assert.True(dict.ContainsKey(new AgentSessionId("session-key")));
        Assert.Equal(42, dict[new AgentSessionId("session-key")]);
        dict.Remove(new AgentSessionId("session-key"));
        Assert.Empty(dict);
    }

    [Fact]
    public void AgentSessionId_GetHashCode_ConsistentWithEquality()
    {
        var a = new AgentSessionId("session-hash");
        var b = new AgentSessionId("session-hash");

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

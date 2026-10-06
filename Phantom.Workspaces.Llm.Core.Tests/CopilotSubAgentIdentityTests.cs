using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSubAgentIdentityTests
{
    [Fact]
    public void EncodedChildSessionId_RestoresSdkAgentAndOriginalInvocationIdentity()
    {
        var id = CopilotSubAgentIdentity.Create("sdk-agent", "spawn-call");
        Assert.True(CopilotSubAgentIdentity.TryRead(id.Value, out var agent, out var invocation));
        Assert.Equal("sdk-agent", agent);
        Assert.Equal("spawn-call", invocation);
        Assert.False(CopilotSubAgentIdentity.TryRead(Guid.NewGuid().ToString("n"), out _, out _));
    }
}

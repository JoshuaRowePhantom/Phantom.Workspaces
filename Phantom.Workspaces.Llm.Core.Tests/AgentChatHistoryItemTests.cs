using System;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatHistoryItemTests
{
    [Fact]
    public void DefaultTimestamp_IsApproximatelyUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var item = new AgentChatHistoryItem { Role = ChatRole.User };
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(item.Timestamp, before, after);
    }
}

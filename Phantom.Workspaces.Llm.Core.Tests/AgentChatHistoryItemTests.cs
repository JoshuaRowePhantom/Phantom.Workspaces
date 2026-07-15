using System;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatHistoryItemTests
{
    [Fact]
    public void Timestamp_IsNullWhenNotSet()
    {
        var item = new AgentChatHistoryItem { Role = ChatRole.User };

        Assert.Null(item.Timestamp);
    }

    [Fact]
    public void Timestamp_ExplicitValue_RoundTrips()
    {
        var expected = new DateTimeOffset(2026, 6, 27, 15, 13, 0, TimeSpan.Zero);
        var item = new AgentChatHistoryItem { Role = ChatRole.User, Timestamp = expected };

        Assert.Equal(expected, item.Timestamp);
    }

    // ── Issue #332: HelpChatRole tests ─────────────────────────────────────────

    [Fact]
    public void HelpChatRole_Value_IsHelp()
    {
        Assert.Equal("help", AgentChatHistoryItem.HelpChatRole.Value);
    }
}

using System.Reflection;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatPerformanceTests
{
    private static bool AreEqual(AgentChatHistoryItem previous, AgentChatHistoryItem current)
        => typeof(AgentChat).GetNestedType("PartialResponseConflator", BindingFlags.NonPublic)!
            .GetMethod("AreStructurallyEqual", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<AgentChatHistoryItem, AgentChatHistoryItem, bool>>()(previous, current);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialResponseConflator_ChangedToolPayload_DoesNotReuseStaleItem(bool isResult)
    {
        AIContent previousContent = isResult
            ? new FunctionResultContent("call", "before")
            : new FunctionCallContent("call", "read", new Dictionary<string, object?> { ["path"] = "before" });
        AIContent currentContent = isResult
            ? new FunctionResultContent("call", "after")
            : new FunctionCallContent("call", "read", new Dictionary<string, object?> { ["path"] = "after" });
        var previous = new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [previousContent] };
        var current = previous with { Contents = [currentContent] };
        Assert.False(AreEqual(previous, current));
        Assert.True(AreEqual(previous, previous with { Contents = [previousContent] }));
    }
}

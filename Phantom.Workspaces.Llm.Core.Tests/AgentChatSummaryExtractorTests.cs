using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatSummaryExtractorTests
{
    private static AgentChatHistoryItem UserItem(string text)
        => new() { Role = ChatRole.User, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem AssistantItem(string text)
        => new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem AssistantToolItem(string toolName)
        => new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call-1", toolName, null)] };

    private static AgentChatRunningItem RunningItemWithText(string text)
    {
        var item = new AgentChatRunningItem();
        item.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)],
        });
        return item;
    }

    private static AgentChatRunningItem RunningItemWithTool(string toolName)
    {
        var item = new AgentChatRunningItem();
        item.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", toolName, null)],
        });
        return item;
    }

    [Fact]
    public void ExtractRunning_WithRunningTextContent_ReturnsTextSummary()
    {
        var history = new List<AgentChatHistoryItem> { UserItem("hello") };
        var runningItems = new List<AgentChatRunningItem> { RunningItemWithText("Thinking about it") };

        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("Thinking about it", textSummary);
    }

    [Fact]
    public void ExtractRunning_WithRunningTextLongerThan100Chars_TruncatesAtWordBoundary()
    {
        // 110-char string with a word boundary at position 98
        var longText = "This is a long sentence that goes well beyond one hundred characters and should be word-broken properly here";
        var history = new List<AgentChatHistoryItem> { UserItem("hello") };
        var runningItems = new List<AgentChatRunningItem> { RunningItemWithText(longText) };

        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.NotNull(textSummary);
        Assert.True(textSummary!.Length <= 102, $"Expected ≤102 chars (100 + ellipsis), got {textSummary.Length}");
        Assert.EndsWith("\u2026", textSummary);
        Assert.DoesNotContain(' ', textSummary[^2..^1]);
    }

    [Fact]
    public void ExtractRunning_WithNoRunningText_FallsBackToAssistantHistory()
    {
        var history = new List<AgentChatHistoryItem>
        {
            UserItem("hello"),
            AssistantItem("I will help you"),
        };
        var runningItems = new List<AgentChatRunningItem>();

        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("I will help you", textSummary);
    }

    [Fact]
    public void ExtractRunning_WithNoRunningOrAssistantText_FallsBackToUserMessage()
    {
        var history = new List<AgentChatHistoryItem> { UserItem("please do this task") };
        var runningItems = new List<AgentChatRunningItem>();

        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("please do this task", textSummary);
    }

    [Fact]
    public void ExtractRunning_WithFunctionCallInRunningItems_ReturnsToolName()
    {
        var history = new List<AgentChatHistoryItem> { UserItem("hello") };
        var runningItems = new List<AgentChatRunningItem> { RunningItemWithTool("read_file") };

        var (_, toolSummary) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("read_file", toolSummary);
    }

    [Fact]
    public void ExtractRunning_WithFunctionCallInHistoryAfterLastUser_ReturnsToolName()
    {
        var history = new List<AgentChatHistoryItem>
        {
            UserItem("hello"),
            AssistantToolItem("write_file"),
            AssistantItem("Done"),
        };
        var runningItems = new List<AgentChatRunningItem>();

        var (_, toolSummary) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("write_file", toolSummary);
    }

    [Fact]
    public void ExtractRunning_WithEmptyEverything_ReturnsNullSummaries()
    {
        var history = new List<AgentChatHistoryItem>();
        var runningItems = new List<AgentChatRunningItem>();

        var (textSummary, toolSummary) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Null(textSummary);
        Assert.Null(toolSummary);
    }

    [Fact]
    public void ExtractRunning_PrefersMostRecentRunningItemText()
    {
        var history = new List<AgentChatHistoryItem> { UserItem("hello") };
        var runningItems = new List<AgentChatRunningItem>
        {
            RunningItemWithText("older text"),
            RunningItemWithText("newer text"),
        };

        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);

        Assert.Equal("newer text", textSummary);
    }

    [Fact]
    public void ExtractRunning_HistoryContainsNullItem_DoesNotThrow()
    {
        // A torn/transient element observed mid-mutation during teardown can be null (issue #1084).
        var history = new List<AgentChatHistoryItem>
        {
            UserItem("hello"),
            null!,
            AssistantItem("I will help you"),
        };
        var runningItems = new List<AgentChatRunningItem>();

        var exception = Record.Exception(() => AgentChatSummaryExtractor.ExtractRunning(history, runningItems));

        Assert.Null(exception);
        var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(history, runningItems);
        Assert.Equal("I will help you", textSummary);
    }

    [Fact]
    public void ExtractRunning_HistoryItemWithNullContents_DoesNotThrow()
    {
        // A partially-built history item can have null Contents (issue #1084, lines 33/66).
        var history = new List<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.User, Contents = null! },
            new() { Role = ChatRole.Assistant, Contents = null! },
        };
        var runningItems = new List<AgentChatRunningItem>();

        var exception = Record.Exception(() => AgentChatSummaryExtractor.ExtractRunning(history, runningItems));

        Assert.Null(exception);
    }

    [Fact]
    public void ExtractRunning_RunningItemWithNullContents_DoesNotThrow()
    {
        // A running item containing a partially-built entry with null Contents must not throw.
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = null! });
        var history = new List<AgentChatHistoryItem> { UserItem("hello") };
        var runningItems = new List<AgentChatRunningItem> { runningItem };

        var exception = Record.Exception(() => AgentChatSummaryExtractor.ExtractRunning(history, runningItems));

        Assert.Null(exception);
    }

    [Fact]
    public void TruncateAtWordBoundary_WithTextExactlyAtMax_ReturnsUnchanged()
    {
        var text = new string('a', 100);

        var result = AgentChatSummaryExtractor.TruncateAtWordBoundary(text, 100);

        Assert.Equal(text, result);
    }

    [Fact]
    public void TruncateAtWordBoundary_WithShortText_ReturnsUnchanged()
    {
        var result = AgentChatSummaryExtractor.TruncateAtWordBoundary("hello world", 100);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void TruncateAtWordBoundary_CutsAtWordBoundary_WhenTextExceedsMax()
    {
        // "hello world foo" — word boundary at position 5 (before "world"), 11 (before "foo")
        var prefix = new string('a', 95);
        var text = prefix + " extra words that push past the limit"; // space at position 95

        var result = AgentChatSummaryExtractor.TruncateAtWordBoundary(text, 100);

        Assert.EndsWith("\u2026", result);
        Assert.True(result.Length <= 102);
        // The cut should be at the space at position 95, yielding the 95 'a' chars + ellipsis
        Assert.Equal(prefix + "\u2026", result);
    }

    [Fact]
    public void TruncateAtWordBoundary_WithNoWordBoundaryInRange_CutsAtMaxChars()
    {
        var text = new string('a', 110); // no spaces

        var result = AgentChatSummaryExtractor.TruncateAtWordBoundary(text, 100);

        Assert.Equal(new string('a', 100) + "\u2026", result);
    }
}

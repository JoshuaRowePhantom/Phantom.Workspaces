using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class RunningSubAgentDisplayTests
{
    private static AgentChatRunningItemCollection CreateRunningItems()
        => new AgentChatRunningItemCollection();

    private static AgentChatRunningItem CreateRunningItem(AgentChatRunningItemCollection collection)
    {
        var item = new AgentChatRunningItem();
        collection.Add(item);
        return item;
    }

    private static AgentChatHistoryItem TextHistoryItem(string text)
        => new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem ToolCallHistoryItem(string toolName)
        => new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-id", toolName)],
        };

    [Fact]
    public void WhenRunningItemAdded_SubscribesToItsItems()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("hello"));

        Assert.Single(sut.RecentActivity);
    }

    [Fact]
    public void WhenRunningItemRemoved_UnsubscribesFromItsItems()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        runningItems.Remove(item);

        item.Items.Add(TextHistoryItem("should be ignored"));

        Assert.Empty(sut.RecentActivity);
    }

    [Fact]
    public void WhenItemWithTextArrives_AddsAgentTextActivityLine()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("some agent output"));

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal(SubAgentActivityKind.AgentText, line.Kind);
        Assert.Equal("some agent output", line.Text);
    }

    [Fact]
    public void WhenItemWithToolCallArrives_AddsToolCallActivityLine()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(ToolCallHistoryItem("read_file"));

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal(SubAgentActivityKind.ToolCall, line.Kind);
        Assert.Equal("read_file", line.Text);
    }

    [Fact]
    public void RecentActivity_IsCappedAtMaxActivityLines()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);

        for (var i = 1; i <= 7; i++)
            item.Items.Add(TextHistoryItem($"line {i}"));

        Assert.Equal(RunningSubAgentDisplay.MaxActivityLines, sut.RecentActivity.Count);
        Assert.Equal("line 3", sut.RecentActivity[0].Text);
        Assert.Equal("line 7", sut.RecentActivity[4].Text);
    }

    [Fact]
    public void ActivityChanged_FiresWhenActivityLineAdded()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var fired = 0;
        sut.ActivityChanged += (_, _) => fired++;

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("hello"));
        item.Items.Add(ToolCallHistoryItem("write_file"));

        Assert.Equal(2, fired);
    }

    [Fact]
    public void ActivityChanged_DoesNotFire_WhenItemHasNoRecognisedContent()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var fired = 0;
        sut.ActivityChanged += (_, _) => fired++;

        var item = CreateRunningItem(runningItems);
        item.Items.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [] });

        Assert.Equal(0, fired);
    }

    [Fact]
    public void WhenRunningItemItemsCollectionChanges_RecentActivityUpdated()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);

        item.Items.Add(TextHistoryItem("first"));
        Assert.Single(sut.RecentActivity);
        Assert.Equal("first", sut.RecentActivity[0].Text);

        item.Items.Add(ToolCallHistoryItem("my_tool"));
        Assert.Equal(2, sut.RecentActivity.Count);
        Assert.Equal(SubAgentActivityKind.ToolCall, sut.RecentActivity[1].Kind);
    }
}

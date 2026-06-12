using System;
using System.Collections.ObjectModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatTextOutputModelTests
{
    [AvaloniaFact]
    public void HistoryAndRunningItems_RenderAsPlainText()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.User,
                Contents = [new TextContent("hello")],
            },
        };
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        var renderedText = string.Empty;
        using var model = new AgentChatTextOutputModel(
            history,
            runningItems,
            () => false,
            text => renderedText = text);

        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(
            new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("thinking...")],
            });
        runningItems.Add(runningItem);

        Assert.Contains("user", renderedText, StringComparison.Ordinal);
        Assert.Contains("hello", renderedText, StringComparison.Ordinal);
        Assert.Contains("assistant (running)", renderedText, StringComparison.Ordinal);
        Assert.Contains("thinking...", renderedText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ReasoningVisibility_RefreshControlsReasoningText()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("answer"),
                    new TextReasoningContent("hidden reasoning"),
                ],
            },
        };
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        var renderedText = string.Empty;
        var isReasoningVisible = false;
        using var model = new AgentChatTextOutputModel(
            history,
            runningItems,
            () => isReasoningVisible,
            text => renderedText = text);

        Assert.DoesNotContain("hidden reasoning", renderedText, StringComparison.Ordinal);

        isReasoningVisible = true;
        model.Refresh();

        Assert.Contains("hidden reasoning", renderedText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ToolCallAndResult_RenderWithPrettyJson()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("workspaces_entity_get", "{ \"value\": 1 }"),
                    new FunctionResultContent("call-1", "{ \"result\": true }"),
                ],
            },
        };
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        var renderedText = string.Empty;
        using var model = new AgentChatTextOutputModel(
            history,
            runningItems,
            () => false,
            text => renderedText = text);

        Assert.Contains("tool call:", renderedText, StringComparison.Ordinal);
        Assert.Contains("\"value\": 1", renderedText, StringComparison.Ordinal);
        Assert.Contains("tool result: call-1", renderedText, StringComparison.Ordinal);
        Assert.Contains("\"result\": true", renderedText, StringComparison.Ordinal);
    }
}

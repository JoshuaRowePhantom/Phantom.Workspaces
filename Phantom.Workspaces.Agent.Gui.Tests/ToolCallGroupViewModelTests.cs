using Avalonia.Headless.XUnit;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ToolCallGroupViewModelTests
{
    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<(string Path, ChatOutputUpdateLocation Location, string Content)> Operations { get; } = [];

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add((path, location, content));

        public void RemoveContent(string path) { }
        public void ScrollToBottom() { }
        public void BeginBatch() { }
        public void EndBatch() { }
    }

    private static AgentChatHistoryItem ToolCallMessage(params string[] toolNames)
    {
        var contents = toolNames.Select(name => new FunctionCallContent(name, name + "_id")).ToList<AIContent>();
        return new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = contents };
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_MixedTools_HeaderListsUniqueToolNames()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("report_intent"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Operations.Clear();

        // Live-add a second, different tool call so the pair is promoted into a group and the
        // group summary is refreshed with the mixed-tools name list.
        history.Add(ToolCallMessage("workspaces_entity_get"));

        var groupSummaryUpdate = sink.Operations.FirstOrDefault(op => op.Path.Contains("-summary"));
        Assert.True(groupSummaryUpdate != default);

        var summaryHtml = groupSummaryUpdate.Content;
        Assert.Contains("2 calls", summaryHtml);

        // Mixed group lists all unique tool names in first-seen order.
        Assert.Contains("tools (", summaryHtml);
        Assert.Contains("report_intent", summaryHtml);
        Assert.Contains("workspaces_entity_get", summaryHtml);
        Assert.True(
            summaryHtml.IndexOf("report_intent", System.StringComparison.Ordinal) <
            summaryHtml.IndexOf("workspaces_entity_get", System.StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_HomogeneousTools_HeaderShowsToolName()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("grep"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Operations.Clear();

        // Live-add a second call of the same tool so promotion refreshes the group summary.
        history.Add(ToolCallMessage("grep"));

        var groupSummaryUpdate = sink.Operations.FirstOrDefault(op => op.Path.Contains("-summary"));
        Assert.True(groupSummaryUpdate != default);

        var summaryHtml = groupSummaryUpdate.Content;
        Assert.Contains("grep", summaryHtml);
        Assert.Contains("2 calls", summaryHtml);

        // Homogeneous group lists the single tool name as "tools (grep)".
        Assert.Contains("tools (", summaryHtml);
        Assert.DoesNotContain("tool call:", summaryHtml);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_MultiCall_RendersGroupWithChildren()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("view"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Operations.Clear();

        history.Add(ToolCallMessage("edit"));

        // When two consecutive tool calls exist, they should be grouped
        var groupSummaryUpdate = sink.Operations.FirstOrDefault(op => op.Path.Contains("-summary"));
        Assert.True(groupSummaryUpdate != default);
        
        var summaryHtml = groupSummaryUpdate.Content;
        Assert.Contains("2 calls", summaryHtml);
        
        // Should contain group structure
        var replaceOps = sink.Operations.Where(op => op.Location == ChatOutputUpdateLocation.Replace).ToList();
        Assert.Contains(replaceOps, op => op.Content.Contains("chat-tool-group"));
    }
}

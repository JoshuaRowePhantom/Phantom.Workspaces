using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_MixedTools_HeaderShowsNeutralLabel()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("report_intent"),
            ToolCallMessage("workspaces_entity_get"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var groupSummaryUpdate = sink.Operations.FirstOrDefault(op => op.Path.Contains("-summary"));
        Assert.True(groupSummaryUpdate != default);
        
        var summaryHtml = groupSummaryUpdate.Content;
        Assert.Contains("2 calls", summaryHtml);
        
        // Should NOT show a single tool name when mixed
        Assert.DoesNotContain("report_intent", summaryHtml);
        Assert.DoesNotContain("workspaces_entity_get", summaryHtml);
        
        // Should show neutral label "tools" not "tool call:"
        Assert.Contains("tools", summaryHtml);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_HomogeneousTools_HeaderShowsToolName()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("grep"),
            ToolCallMessage("grep"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var groupSummaryUpdate = sink.Operations.FirstOrDefault(op => op.Path.Contains("-summary"));
        Assert.True(groupSummaryUpdate != default);
        
        var summaryHtml = groupSummaryUpdate.Content;
        Assert.Contains("grep", summaryHtml);
        Assert.Contains("2 calls", summaryHtml);
        
        // When homogeneous, should use "tool call:" prefix
        Assert.Contains("tool call:", summaryHtml);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroup_MultiCall_RendersGroupWithChildren()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("view"),
            ToolCallMessage("edit"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

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

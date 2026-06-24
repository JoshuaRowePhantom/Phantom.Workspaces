using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatMessageDocumentModelTests
{
    private static AgentChatHistoryItem ToolCallThenText(string callId, string text)
        => new()
        {
            Role = ChatRole.Assistant,
            Contents = new AIContent[]
            {
                new FunctionCallContent(callId, "search", new Dictionary<string, object?> { ["query"] = "phantom" }),
                new TextContent(text),
            },
        };

    private static Section ContentSection(ChatMessageDocumentModel model)
        => (Section)model.Section.Blocks[1];

    // Reproduces the O(n^2) streaming render: as the trailing text grows, the leading (unchanged)
    // tool-call content must not be re-rendered (no JSON re-parse); its rendered blocks are reused.
    [AvaloniaFact]
    public void Update_WhenLeadingContentUnchanged_ReusesRenderedBlocks()
    {
        var model = new ChatMessageDocumentModel(ToolCallThenText("call-1", "partial"), () => false);
        var content = ContentSection(model);
        var toolMetaBlock = content.Blocks[0];
        var toolArgumentsBlock = content.Blocks[1];

        model.Update(ToolCallThenText("call-1", "partial and then some more"));

        var contentAfter = ContentSection(model);
        Assert.Same(toolMetaBlock, contentAfter.Blocks[0]);
        Assert.Same(toolArgumentsBlock, contentAfter.Blocks[1]);
    }

    [AvaloniaFact]
    public void Update_WhenTrailingContentChanges_RerendersOnlyThatContent()
    {
        var model = new ChatMessageDocumentModel(ToolCallThenText("call-1", "partial"), () => false);
        var content = ContentSection(model);
        var trailingTextBlock = content.Blocks[2];

        model.Update(ToolCallThenText("call-1", "partial and then some more"));

        var contentAfter = ContentSection(model);
        Assert.NotSame(trailingTextBlock, contentAfter.Blocks[2]);
    }

    [AvaloniaFact]
    public void Update_WhenNothingChanges_ReusesAllBlocks()
    {
        var model = new ChatMessageDocumentModel(ToolCallThenText("call-1", "stable"), () => false);
        var content = ContentSection(model);
        var before = content.Blocks.ToArray();

        model.Update(ToolCallThenText("call-1", "stable"));

        var contentAfter = ContentSection(model);
        Assert.Equal(before.Length, contentAfter.Blocks.Count);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.Same(before[index], contentAfter.Blocks[index]);
        }
    }
}

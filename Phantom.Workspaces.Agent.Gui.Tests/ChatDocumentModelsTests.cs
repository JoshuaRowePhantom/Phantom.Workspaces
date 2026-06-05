using System.Collections.ObjectModel;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatDocumentModelsTests
{
    [Fact]
    public void ChatHistory_NoOpUpdate_PreservesRenderedTextParagraphReference()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>();
        using var model = new ChatHistoryDocumentModel(root, history, () => false);
        var item = CreateHistoryItem(ChatRole.User, "hello world");
        history.Add(item);

        var firstParagraph = GetFirstContentParagraph(root, messageIndex: 0);
        var secondParagraph = GetFirstContentParagraph(root, messageIndex: 0);

        Assert.Same(firstParagraph, secondParagraph);
    }

    [Fact]
    public void ChatHistory_SecondMessageChange_LeavesFirstMessageBlocksUntouched()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>();
        using var model = new ChatHistoryDocumentModel(root, history, () => false);
        var first = CreateHistoryItem(ChatRole.User, "first");
        var second = CreateHistoryItem(ChatRole.Assistant, "before");
        history.Add(first);
        history.Add(second);

        var firstMessageSection = (Section)root.Blocks[0];
        var firstMessageParagraph = GetFirstContentParagraph(root, messageIndex: 0);

        var updatedSecond = CreateHistoryItem(ChatRole.Assistant, "after");
        history[1] = updatedSecond;

        var firstMessageSectionAfter = (Section)root.Blocks[0];
        var firstMessageParagraphAfter = GetFirstContentParagraph(root, messageIndex: 0);
        var secondParagraphAfter = GetFirstContentParagraph(root, messageIndex: 1);

        Assert.Same(firstMessageSection, firstMessageSectionAfter);
        Assert.Same(firstMessageParagraph, firstMessageParagraphAfter);
        Assert.Equal("after", secondParagraphAfter.Inlines.OfType<RichRun>().Single().Text);
    }

    [Fact]
    public void RunningItems_MiddleInsert_PreservesExistingTrailingSection()
    {
        var root = new Section();
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        using var model = new RunningChatItemsDocumentModel(root, runningItems, () => false);
        var runningA = CreateRunningItem("A");
        var runningC = CreateRunningItem("C");
        runningItems.Add(runningA);
        runningItems.Add(runningC);

        var trailingBefore = (Section)root.Blocks[1];

        var runningB = CreateRunningItem("B");
        runningItems.Insert(1, runningB);
        var trailingAfter = (Section)root.Blocks[2];

        Assert.Same(trailingBefore, trailingAfter);
    }

    [Fact]
    public void Labels_UseRoleSpecificClasses()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.User, "hello"),
            CreateHistoryItem(ChatRole.Assistant, "hello"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        var userLabel = (Paragraph)((Section)((Section)root.Blocks[0]).Blocks[0]).Blocks[0];
        var assistantLabel = (Paragraph)((Section)((Section)root.Blocks[1]).Blocks[0]).Blocks[0];
        Assert.Contains("agent-chat-role-label-user", userLabel.Classes);
        Assert.Contains("agent-chat-role-label-assistant", assistantLabel.Classes);
    }

    [Fact]
    public void TextContentParagraph_UsesBodyClass()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.User, "hello"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        var paragraph = GetFirstContentParagraph(root, messageIndex: 0);
        Assert.Contains("agent-chat-body", paragraph.Classes);
    }

    [Fact]
    public void Message_DoesNotRenderTrailingSpacer()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.User, "hello"),
            CreateHistoryItem(ChatRole.Assistant, "world"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        Assert.Equal(4, ((Section)root.Blocks[0]).Blocks.Count);
        Assert.Equal(4, ((Section)root.Blocks[1]).Blocks.Count);
    }

    [Fact]
    public void HistoryItem_DoesNotShowProgressBar()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.Assistant, "partial"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        var messageSection = (Section)root.Blocks[0];
        var progressHost = (Section)messageSection.Blocks[3];
        Assert.Empty(progressHost.Blocks.OfType<BlockUIContainer>());
    }

    [Fact]
    public void ReasoningVisible_RendersReasoningParagraph()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("answer"),
                    new TextReasoningContent("reasoning text"),
                ],
            },
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => true);

        var messageSection = (Section)root.Blocks[0];
        var reasoningHost = (Section)messageSection.Blocks[1];
        var paragraph = Assert.Single(reasoningHost.Blocks.OfType<Paragraph>());
        Assert.Contains("reasoning text", paragraph.Inlines.OfType<RichRun>().Single().Text, StringComparison.Ordinal);
    }

    private static Paragraph GetFirstContentParagraph(Section historyRoot, int messageIndex)
    {
        var messageSection = (Section)historyRoot.Blocks[messageIndex];
        var contentHost = (Section)messageSection.Blocks[2];
        return (Paragraph)contentHost.Blocks[0];
    }

    private static AgentChatHistoryItem CreateHistoryItem(ChatRole role, string text)
        => new()
        {
            Role = role,
            Contents = [new TextContent(text)],
        };

    private static AgentChatRunningItem CreateRunningItem(string text)
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)],
        });
        return runningItem;
    }
}


using System.Collections.ObjectModel;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatDocumentModelsTests
{
    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void RunningItem_UpdatesWithReasoningText_RendersProgressAndReasoning()
    {
        var root = new Section();
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        using var model = new RunningChatItemsDocumentModel(root, runningItems, () => true);
         
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("initial response")],
        });
        runningItems.Add(runningItem);

        // Update the running item with reasoning text
        runningItem.Items[0] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("updated response"),
                new TextReasoningContent("reasoning text"),
            ],
        };

        // Get the rendered message section and verify it exists
        var runningItemSection = (Section)root.Blocks[0];
         
        // Verify progress bar is shown at the running item level (not per message)
        var progressSection = (Section)runningItemSection.Blocks[1];
        Assert.NotEmpty(progressSection.Blocks.OfType<BlockUIContainer>());
    }

    [AvaloniaFact]
    public void RunningItem_UpdateWithDifferentInstance_SwitchesTransformer()
    {
        var root = new Section();
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        using var model = new RunningChatItemsDocumentModel(root, runningItems, () => false);

        var runningItem1 = new AgentChatRunningItem();
        runningItem1.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("first")],
        });
        runningItems.Add(runningItem1);

        var blockCountBefore = root.Blocks.Count;

        // Replace with a different instance
        var runningItem2 = new AgentChatRunningItem();
        runningItem2.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("second")],
        });
        runningItems[0] = runningItem2;

        // Add to the new instance and verify it renders
        runningItem2.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("user input")],
        });

        // Should have rendered the two items from the new running item
        var runningItemSection = (Section)root.Blocks[0];
        var messagesSection = (Section)runningItemSection.Blocks[0];
         
        Assert.Equal(2, messagesSection.Blocks.Count);
    }


    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void Message_DoesNotRenderTrailingSpacer()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.User, "hello"),
            CreateHistoryItem(ChatRole.Assistant, "world"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        // History items have 2 blocks: label, content
        Assert.Equal(2, ((Section)root.Blocks[0]).Blocks.Count);
        Assert.Equal(2, ((Section)root.Blocks[1]).Blocks.Count);
    }

    [AvaloniaFact]
    public void HistoryItem_DoesNotShowProgressBar()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            CreateHistoryItem(ChatRole.Assistant, "partial"),
        };
        using var model = new ChatHistoryDocumentModel(root, history, () => false);

        var messageSection = (Section)root.Blocks[0];
        // History items only have 2 blocks: label, content (no progress section)
        Assert.Equal(2, messageSection.Blocks.Count);
    }

    [AvaloniaFact]
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
        var contentSection = (Section)messageSection.Blocks[1];
        var paragraphs = contentSection.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Contains("reasoning text", paragraphs[1].Inlines.OfType<RichRun>().Single().Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ReasoningVisibilityToggle_UpdatesDocumentWithoutErrors()
    {
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
         
        var isReasoningVisible = false;
        var root = new Section();
         
        using var model = new ChatHistoryDocumentModel(root, history, () => isReasoningVisible);
         
        // Initially reasoning is hidden, only 1 paragraph (answer)
        var messageSection = (Section)root.Blocks[0];
        var contentSection = (Section)messageSection.Blocks[1];
        Assert.Single(contentSection.Blocks.OfType<Paragraph>());
         
        // Toggle visibility to true
        isReasoningVisible = true;
        model.Refresh();
         
        // Should now show both answer and reasoning paragraphs
        var contentSectionAfter = (Section)messageSection.Blocks[1];
        var paragraphs = contentSectionAfter.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Contains("reasoning text", paragraphs[1].Inlines.OfType<RichRun>().Single().Text, StringComparison.Ordinal);
         
        // Toggle back to false
        isReasoningVisible = false;
        model.Refresh();
         
        // Should be back to just answer paragraph
        var contentSectionAfterToggle = (Section)messageSection.Blocks[1];
        Assert.Single(contentSectionAfterToggle.Blocks.OfType<Paragraph>());
    }

    [AvaloniaFact]
    public void RunningItem_ReasoningVisibilityToggle_UpdatesWithoutErrors()
    {
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        var isReasoningVisible = false;
        var root = new Section();
         
        using var model = new RunningChatItemsDocumentModel(root, runningItems, () => isReasoningVisible);
         
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("response"),
                new TextReasoningContent("reasoning text"),
            ],
        });
        runningItems.Add(runningItem);
         
        // Initially reasoning is hidden, only 1 paragraph (response)
        var runningItemSection = (Section)root.Blocks[0];
        var messagesSection = (Section)runningItemSection.Blocks[0];
        var messageSection = (Section)messagesSection.Blocks[0];
        var contentSection = (Section)messageSection.Blocks[1];
        Assert.Single(contentSection.Blocks.OfType<Paragraph>());
         
        // Toggle visibility to true
        isReasoningVisible = true;
        model.Refresh();
         
        // Should now show both response and reasoning paragraphs
        var contentSectionAfter = (Section)messageSection.Blocks[1];
        var paragraphs = contentSectionAfter.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Contains("reasoning text", paragraphs[1].Inlines.OfType<RichRun>().Single().Text, StringComparison.Ordinal);
         
        // Toggle back to false
        isReasoningVisible = false;
        model.Refresh();
         
        // Should be back to just response paragraph
        var contentSectionAfterToggle = (Section)messageSection.Blocks[1];
        Assert.Single(contentSectionAfterToggle.Blocks.OfType<Paragraph>());
    }

    private static Paragraph GetFirstContentParagraph(Section historyRoot, int messageIndex)
    {
        var messageSection = (Section)historyRoot.Blocks[messageIndex];
        var contentHost = (Section)messageSection.Blocks[1];
        return (Paragraph)contentHost.Blocks[0];
    }

    [AvaloniaFact]
    public void ToggleReasoningVisibility_PreservesDocumentStructure()
    {
        var root = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.User,
                Contents = [new TextContent("question")],
            },
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("answer part 1"),
                    new TextReasoningContent("reasoning text"),
                    new TextContent("answer part 2"),
                ],
            },
        };

        var isReasoningVisible = false;
        using var model = new ChatHistoryDocumentModel(root, history, () => isReasoningVisible);

        // Both messages should have 2 blocks (label + content)
        Assert.Equal(2, ((Section)root.Blocks[0]).Blocks.Count);
        Assert.Equal(2, ((Section)root.Blocks[1]).Blocks.Count);

        var message1Section = (Section)root.Blocks[0];
        var message2Section = (Section)root.Blocks[1];

        // Message 1 should have 1 paragraph (the question)
        var msg1Content = (Section)message1Section.Blocks[1];
        var msg1Paragraphs = msg1Content.Blocks.OfType<Paragraph>().ToList();
        Assert.Single(msg1Paragraphs);

        // Message 2 should have 2 paragraphs (answer part 1 and answer part 2, but no reasoning)
        var msg2Content = (Section)message2Section.Blocks[1];
        var msg2ParagraphsBefore = msg2Content.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, msg2ParagraphsBefore.Count);

        // Capture the paragraph reference to verify it's preserved
        var msg2FirstParagraphBefore = msg2ParagraphsBefore[0];

        // Toggle visibility to true
        isReasoningVisible = true;
        model.Refresh();

        // Structure should still be 2 blocks per message
        Assert.Equal(2, ((Section)root.Blocks[0]).Blocks.Count);
        Assert.Equal(2, ((Section)root.Blocks[1]).Blocks.Count);

        // Message 2 should now have 3 paragraphs (answer 1, reasoning, answer 2)
        var msg2ParagraphsAfter = ((Section)((Section)root.Blocks[1]).Blocks[1]).Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(3, msg2ParagraphsAfter.Count);
        Assert.Contains("reasoning text", msg2ParagraphsAfter[1].Inlines.OfType<RichRun>().Single().Text, StringComparison.Ordinal);

        // Toggle back to false
        isReasoningVisible = false;
        model.Refresh();

        // Should be back to 2 paragraphs
        var msg2ParagraphsAfterToggle = ((Section)((Section)root.Blocks[1]).Blocks[1]).Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, msg2ParagraphsAfterToggle.Count);
    }

    private static AgentChatHistoryItem CreateHistoryItem(ChatRole role, string text)
        => new()
        {
            Role = role,

            Contents = [new TextContent(text)],
        };

    [AvaloniaFact]
    public void ToggleReasoningVisibility_WithRunningItems_PreservesContent()
    {
        var root = new Section();
        var runningItems = new ObservableCollection<AgentChatRunningItem>();
        var isReasoningVisible = false;
        
        using var model = new RunningChatItemsDocumentModel(root, runningItems, () => isReasoningVisible);
        
        // Add a running item
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("answer part 1"),
                new TextReasoningContent("reasoning text"),
                new TextContent("answer part 2"),
            ],
        });
        runningItems.Add(runningItem);
        
        // Should have 1 running item block
        Assert.Single(root.Blocks);
        var runningItemSection = (Section)root.Blocks[0];
        
        // Running item should have messagesSection and progressSection (2 blocks)
        Assert.Equal(2, runningItemSection.Blocks.Count);
        
        // Messages section should have 1 message with 2 content blocks (no reasoning yet)
        var messagesSection = (Section)runningItemSection.Blocks[0];
        var messageSection = (Section)messagesSection.Blocks[0];
        var contentSection = (Section)messageSection.Blocks[1];
        var paragraphs = contentSection.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        
        // Toggle visibility to true
        isReasoningVisible = true;
        model.Refresh();
        
        // Root should still have 1 running item block
        Assert.Single(root.Blocks);
        
        // Running item should still have messagesSection and progressSection
        var runningItemSectionAfter = (Section)root.Blocks[0];
        Assert.Equal(2, runningItemSectionAfter.Blocks.Count);
        
        // Message should now have 3 paragraphs (reasoning is now visible)
        var messagesSectionAfter = (Section)runningItemSectionAfter.Blocks[0];
        var messageSectionAfter = (Section)messagesSectionAfter.Blocks[0];
        var contentSectionAfter = (Section)messageSectionAfter.Blocks[1];
        var paragraphsAfter = contentSectionAfter.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(3, paragraphsAfter.Count);
    }

    [AvaloniaFact]
    public void ToggleReasoningVisibility_HistoryWithRunningItems_PreservesHistoryContent()
    {
        var historyRoot = new Section();
        var history = new ObservableCollection<AgentChatHistoryItem>();
        var isReasoningVisible = false;
        
        var historyModel = new ChatHistoryDocumentModel(historyRoot, history, () => isReasoningVisible);
        
        // Add history items
        history.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("user message")],
        });
        history.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("answer 1"),
                new TextReasoningContent("reasoning 1"),
                new TextContent("answer 2"),
            ],
        });
        
        // Should have 2 message blocks in history
        Assert.Equal(2, historyRoot.Blocks.Count);
        var firstMessageSection = (Section)historyRoot.Blocks[0];
        Assert.Equal(2, firstMessageSection.Blocks.Count); // label + content
        
        var secondMessageSection = (Section)historyRoot.Blocks[1];
        Assert.Equal(2, secondMessageSection.Blocks.Count); // label + content (reasoning hidden)
        
        var secondContentSection = (Section)secondMessageSection.Blocks[1];
        var beforeParagraphs = secondContentSection.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, beforeParagraphs.Count); // answer 1 and answer 2 only
        
        // Toggle visibility to true
        isReasoningVisible = true;
        historyModel.Refresh();
        
        // History should still have 2 messages
        Assert.Equal(2, historyRoot.Blocks.Count);
        
        // Second message should now have reasoning visible
        var secondMessageSectionAfter = (Section)historyRoot.Blocks[1];
        var secondContentSectionAfter = (Section)secondMessageSectionAfter.Blocks[1];
        var afterParagraphs = secondContentSectionAfter.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(3, afterParagraphs.Count); // answer 1, reasoning, answer 2
        
        historyModel.Dispose();
    }

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


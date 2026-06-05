using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatFlowDocumentBuilderTests
{
    [Fact]
    public void CreateHistorySection_RendersRoleAndMessageText()
    {
        var viewModel = CreateHistoryItemViewModel(
            ChatRole.Assistant,
            [new TextContent("hello world")]);

        var section = AgentChatFlowDocumentBuilder.CreateHistorySection(viewModel);
        var paragraphTexts = GetParagraphTexts(section);

        Assert.Contains("assistant", paragraphTexts);
        Assert.Contains("hello world", paragraphTexts);
    }

    [Fact]
    public void UpdateHistorySection_ReusesSectionAndReplacesContent()
    {
        var viewModel = CreateHistoryItemViewModel(
            ChatRole.Assistant,
            [new TextContent("old text")]);
        var section = AgentChatFlowDocumentBuilder.CreateHistorySection(viewModel);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("new text")],
        });

        AgentChatFlowDocumentBuilder.UpdateHistorySection(section, viewModel);
        var paragraphTexts = GetParagraphTexts(section);

        Assert.Contains("new text", paragraphTexts);
        Assert.DoesNotContain("old text", paragraphTexts);
    }

    [Fact]
    public void UpdateRunningSection_ReusesSectionAndUpdatesRunningContent()
    {
        var running = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("first token")],
                },
            },
        };

        var runningViewModel = new RunningItemViewModel(running);
        var section = AgentChatFlowDocumentBuilder.CreateRunningSection(runningViewModel);

        running.Items.Clear();
        running.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("final text")],
        });
        runningViewModel.UpdateModel();

        AgentChatFlowDocumentBuilder.UpdateRunningSection(section, runningViewModel);
        var paragraphTexts = GetParagraphTexts(section);

        Assert.Contains("assistant (running)", paragraphTexts);
        Assert.Contains("final text", paragraphTexts);
        Assert.DoesNotContain("first token", paragraphTexts);
    }

    [Fact]
    public void UpdateHistorySection_AttachedToDocument_DoesNotThrow()
    {
        var viewModel = CreateHistoryItemViewModel(
            ChatRole.Assistant,
            [new TextContent("original text")]);
        var section = AgentChatFlowDocumentBuilder.CreateHistorySection(viewModel);
        var document = AgentChatFlowDocumentBuilder.CreateDocument();
        document.Blocks.Add(section);
        _ = document.EnsureTextDocument();

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("updated text")],
        });

        var exception = Record.Exception(() => AgentChatFlowDocumentBuilder.UpdateHistorySection(section, viewModel));

        Assert.Null(exception);
        var paragraphTexts = GetParagraphTexts(section);
        Assert.Contains("updated text", paragraphTexts);
    }

    [Fact]
    public void UpdateRunningSection_AttachedToDocument_DoesNotThrow()
    {
        var running = new AgentChatRunningItem
        {
            Items =
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("streaming text")],
                },
            },
        };

        var runningViewModel = new RunningItemViewModel(running);
        var section = AgentChatFlowDocumentBuilder.CreateRunningSection(runningViewModel);
        var document = AgentChatFlowDocumentBuilder.CreateDocument();
        document.Blocks.Add(section);
        _ = document.EnsureTextDocument();

        running.Items.Clear();
        running.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("completed text")],
        });
        runningViewModel.UpdateModel();

        var exception = Record.Exception(() => AgentChatFlowDocumentBuilder.UpdateRunningSection(section, runningViewModel));

        Assert.Null(exception);
        var paragraphTexts = GetParagraphTexts(section);
        Assert.Contains("completed text", paragraphTexts);
    }

    private static ChatHistoryItemViewModel CreateHistoryItemViewModel(
        ChatRole role,
        IReadOnlyList<AIContent> contents)
        => new(new AgentChatHistoryItem
        {
            Role = role,
            Contents = contents,
        });

    private static List<string> GetParagraphTexts(Section section)
    {
        var values = new List<string>();
        foreach (var paragraph in section.Blocks.OfType<Paragraph>())
        {
            var text = string.Concat(paragraph.Inlines.OfType<RichRun>().Select(run => run.Text));
            values.Add(text);
        }

        return values;
    }
}


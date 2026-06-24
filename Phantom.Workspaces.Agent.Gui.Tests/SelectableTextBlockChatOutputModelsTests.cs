using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SelectableTextBlockChatOutputModelsTests
{
    [AvaloniaFact]
    public void ToolContent_DefaultsToCollapsed_WithEmptyDataSpan()
    {
        using var model = new ToolContentSelectableInlineModel(
            "tool call: search",
            () => "data",
            initiallyExpanded: false);

        Assert.False(model.IsExpanded);
        Assert.Empty(model.DataSpan.Inlines);
    }

    [AvaloniaFact]
    public void ToolContent_Expanding_PopulatesDataSpan_Collapsing_ClearsIt()
    {
        using var model = new ToolContentSelectableInlineModel(
            "tool call: search",
            () => "payload text",
            initiallyExpanded: false);

        model.SetExpanded(true);

        Assert.True(model.IsExpanded);
        Assert.Equal("payload text", GetDataText(model));

        model.SetExpanded(false);

        Assert.False(model.IsExpanded);
        Assert.Empty(model.DataSpan.Inlines);
    }

    [AvaloniaFact]
    public void ToolContent_PopulatesLazily_FactoryNotInvokedWhileCollapsed()
    {
        var factoryInvocations = 0;
        using var model = new ToolContentSelectableInlineModel(
            "tool result: c1",
            () =>
            {
                factoryInvocations++;
                return "value";
            },
            initiallyExpanded: false);

        Assert.Equal(0, factoryInvocations);

        model.SetExpanded(true);
        model.SetExpanded(false);
        model.SetExpanded(true);

        Assert.Equal(1, factoryInvocations);
    }

    [AvaloniaFact]
    public void ToolContent_JsonResult_IsPrettyPrinted()
    {
        using var model = new ToolContentSelectableInlineModel(
            "tool result: c1",
            () => DocumentBlockUtilities.PrettyJson("{\"alpha\":1,\"beta\":\"two\"}"),
            initiallyExpanded: true);

        var text = GetDataText(model);

        Assert.Contains("\n", text);
        Assert.Contains("\"alpha\": 1", text);
        Assert.Contains("\"beta\": \"two\"", text);
    }

    [AvaloniaFact]
    public void ToolContent_NonJsonResult_IsShownAsRawText()
    {
        using var model = new ToolContentSelectableInlineModel(
            "tool result: c1",
            () => DocumentBlockUtilities.PrettyJson("not json at all"),
            initiallyExpanded: true);

        Assert.Equal("not json at all", GetDataText(model));
    }

    [AvaloniaFact]
    public void Message_RendersToolCallCollapsed_WithoutDataInlines()
    {
        var item = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["query"] = "cats" }),
            ],
        };

        var model = new ChatMessageSelectableInlineModel(item, () => false);

        var toolSpan = FindToolSpan(model.Span);
        var dataSpan = (Span)toolSpan.Inlines[1];
        Assert.Empty(dataSpan.Inlines);
    }

    [AvaloniaFact]
    public void Message_ToolExpansionState_PersistsAcrossReRender()
    {
        var item = new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents =
            [
                new FunctionResultContent("call-1", "{\"ok\":true}"),
            ],
        };

        var model = new ChatMessageSelectableInlineModel(item, () => false);

        var toolSpanBefore = FindToolSpan(model.Span);
        var dataSpanBefore = (Span)toolSpanBefore.Inlines[1];
        Assert.Empty(dataSpanBefore.Inlines);

        // Simulate the user expanding the tool result.
        var toggleBefore = (ToggleButton)((InlineUIContainer)toolSpanBefore.Inlines[0]).Child!;
        toggleBefore.IsChecked = true;
        Assert.NotEmpty(dataSpanBefore.Inlines);

        // A streaming update re-renders the message; the expansion must survive.
        model.Update(item);

        var toolSpanAfter = FindToolSpan(model.Span);
        var dataSpanAfter = (Span)toolSpanAfter.Inlines[1];
        Assert.NotEmpty(dataSpanAfter.Inlines);
        Assert.Contains("\"ok\": true", GetSpanText(dataSpanAfter));
    }

    [AvaloniaFact]
    public void Message_RendersDiagnosticAsCollapsibleExpander()
    {
        var item = new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents =
            [
                new TextContent("Opened toolset 'workspace-entity'. Loaded tools:\n- a\n- b"),
            ],
        };

        var model = new ChatMessageSelectableInlineModel(item, () => false);

        // Rendered as a collapsible tool-style span, collapsed by default with no body inlines.
        var diagnosticSpan = FindToolSpan(model.Span);
        var dataSpan = (Span)diagnosticSpan.Inlines[1];
        Assert.Empty(dataSpan.Inlines);

        var toggle = (ToggleButton)((InlineUIContainer)diagnosticSpan.Inlines[0]).Child!;
        Assert.Contains("Opened toolset 'workspace-entity'. Loaded tools:", toggle.Content?.ToString(), StringComparison.Ordinal);

        // Expanding reveals the remaining lines (everything after the header line).
        toggle.IsChecked = true;
        var bodyText = GetSpanText(dataSpan);
        Assert.Contains("- a", bodyText, StringComparison.Ordinal);
        Assert.Contains("- b", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("Opened toolset", bodyText, StringComparison.Ordinal);
    }

    // Reproduces the O(n^2) streaming render for the selectable path: as the trailing text grows,
    // the unchanged leading tool content must not be recreated; its inline (and tool model) is reused.
    [AvaloniaFact]
    public void Message_Update_WhenLeadingContentUnchanged_ReusesToolInline()
    {
        static AgentChatHistoryItem Make(string text) => new()
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["query"] = "cats" }),
                new TextContent(text),
            ],
        };

        var model = new ChatMessageSelectableInlineModel(Make("partial"), () => false);
        var toolSpanBefore = FindToolSpan(model.Span);

        model.Update(Make("partial and then some more"));

        var toolSpanAfter = FindToolSpan(model.Span);
        Assert.Same(toolSpanBefore, toolSpanAfter);
    }

    [AvaloniaFact]
    public void Message_Update_WhenNothingChanges_ReusesAllInlines()
    {
        static AgentChatHistoryItem Make() => new()
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["query"] = "cats" }),
                new TextContent("stable"),
            ],
        };

        var model = new ChatMessageSelectableInlineModel(Make(), () => false);
        var before = model.Span.Inlines.ToArray();

        model.Update(Make());

        var after = model.Span.Inlines.ToArray();
        Assert.Equal(before.Length, after.Length);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.Same(before[index], after[index]);
        }
    }

    private static Span FindToolSpan(Span messageSpan)
        => messageSpan.Inlines
            .OfType<Span>()
            .Single(span => span.Classes.Contains("agent-chat-selectable-tool"));

    private static string GetDataText(ToolContentSelectableInlineModel model)
        => GetSpanText(model.DataSpan);

    private static string GetSpanText(Span span)
        => string.Concat(span.Inlines.OfType<Run>().Select(run => run.Text));
}

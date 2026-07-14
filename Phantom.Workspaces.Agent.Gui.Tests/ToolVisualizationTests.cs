using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ToolVisualizationTests
{
    // --- CompositeToolVisualizerFactory ---

    [Fact]
    public void CompositeToolVisualizerFactory_ReturnsFirstNonNull()
    {
        var summary = new Summary("found it");
        var factory = CompositeToolVisualizerFactory.Combine(
            new StubFactory(null),
            new StubFactory(summary),
            new StubFactory(new Summary("should not reach")));

        var result = factory.Visualize(new ToolVisualizationContext(new TextContent("x")));

        Assert.Same(summary, result);
    }

    [Fact]
    public void CompositeToolVisualizerFactory_ReturnsNull_WhenAllReturnNull()
    {
        var factory = CompositeToolVisualizerFactory.Combine(
            new StubFactory(null),
            new StubFactory(null));

        var result = factory.Visualize(new ToolVisualizationContext(new TextContent("x")));

        Assert.Null(result);
    }

    [Fact]
    public void CompositeToolVisualizerFactory_RespectsOrder_FirstFactoryWins()
    {
        var first = new Summary("first");
        var second = new Summary("second");
        var factory = CompositeToolVisualizerFactory.Combine(
            new StubFactory(first),
            new StubFactory(second));

        var result = factory.Visualize(new ToolVisualizationContext(new TextContent("x")));

        Assert.Same(first, result);
    }

    // --- ToolVisualizationInterpreter ---

    [Fact]
    public void ToolVisualizationInterpreter_NullResult_ReturnsNull()
    {
        var html = ToolVisualizationInterpreter.Interpret(null, "c0", statusSink: null);

        Assert.Null(html);
    }

    [Fact]
    public void ToolVisualizationInterpreter_Summary_RendersExpandedDetailsElement()
    {
        var summary = new Summary("my label", "<b>body</b>");

        var html = ToolVisualizationInterpreter.Interpret(summary, "c0", statusSink: null);

        Assert.NotNull(html);
        Assert.Contains("<details", html, StringComparison.Ordinal);
        Assert.Contains("open", html, StringComparison.Ordinal);
        Assert.Contains("<summary class=\"chat-collapsible-summary\">my label</summary>", html, StringComparison.Ordinal);
        Assert.Contains("<b>body</b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolVisualizationInterpreter_Summary_NoBody_RendersWithoutBodyDiv()
    {
        var summary = new Summary("label only");

        var html = ToolVisualizationInterpreter.Interpret(summary, "c0", statusSink: null);

        Assert.NotNull(html);
        Assert.DoesNotContain("chat-collapsible-body", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolVisualizationInterpreter_StatusUpdate_NoSummary_ReturnsEmptyString_AndCallsSink()
    {
        var update = new StatusUpdate(AgentStatusField.Intent, "doing something", ChatSummary: null);
        var sink = new RecordingStatusSink();

        var html = ToolVisualizationInterpreter.Interpret(update, "c0", sink);

        Assert.Equal(string.Empty, html);
        Assert.Single(sink.Updates);
        Assert.Equal(AgentStatusField.Intent, sink.Updates[0].Field);
        Assert.Equal("doing something", sink.Updates[0].Value);
    }

    [Fact]
    public void ToolVisualizationInterpreter_StatusUpdate_WithSummary_EmitsCollapsedDetails_AndCallsSink()
    {
        var update = new StatusUpdate(AgentStatusField.Intent, "val", ChatSummary: "chat label");
        var sink = new RecordingStatusSink();

        var html = ToolVisualizationInterpreter.Interpret(update, "c0", sink);

        Assert.NotNull(html);
        Assert.NotEmpty(html);
        Assert.Contains("<details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("open", html, StringComparison.Ordinal);
        Assert.Contains("chat label", html, StringComparison.Ordinal);
        Assert.Single(sink.Updates);
        Assert.Equal("val", sink.Updates[0].Value);
    }

    // --- CopilotToolVisualizerFactory ---

    [Fact]
    public void CopilotToolVisualizerFactory_ReportIntent_FunctionCall_ReturnsStatusUpdate()
    {
        var factory = new CopilotToolVisualizerFactory();
        var call = new FunctionCallContent("call-1", "report_intent",
            new Dictionary<string, object?> { ["intent"] = "analyzing code" });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        var update = Assert.IsType<StatusUpdate>(result);
        Assert.Equal(AgentStatusField.Intent, update.Field);
        Assert.Equal("analyzing code", update.Value);
        Assert.Null(update.ChatSummary);
    }

    [Fact]
    public void CopilotToolVisualizerFactory_ReportIntent_FunctionResult_ReturnsNull()
    {
        var factory = new CopilotToolVisualizerFactory();
        var result = new FunctionResultContent("call-1", "ok");

        var visualization = factory.Visualize(new ToolVisualizationContext(result));

        Assert.Null(visualization);
    }

    [Fact]
    public void CopilotToolVisualizerFactory_UnknownTool_ReturnsNull()
    {
        var factory = new CopilotToolVisualizerFactory();
        var call = new FunctionCallContent("call-1", "some_other_tool",
            new Dictionary<string, object?> { ["param"] = "value" });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        Assert.Null(result);
    }

    // --- AgentSessionVisualizerFactory ---

    [Fact]
    public void AgentSessionVisualizerFactory_AgentSessionCreate_ReturnsSummary()
    {
        var factory = new AgentSessionVisualizerFactory();
        var call = new FunctionCallContent("call-1", "agent_session_create",
            new Dictionary<string, object?> { ["initial_message"] = "hello subagent" });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        var summary = Assert.IsType<Summary>(result);
        Assert.Contains("subagent", summary.Label, StringComparison.Ordinal);
        Assert.Contains("hello", summary.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionVisualizerFactory_AgentSessionSend_ReturnsSummary()
    {
        var factory = new AgentSessionVisualizerFactory();
        var call = new FunctionCallContent("call-1", "agent_session_send",
            new Dictionary<string, object?>
            {
                ["session_id"] = "abc12345xyz",
                ["text"] = "process this task",
            });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        var summary = Assert.IsType<Summary>(result);
        Assert.Contains("abc12345", summary.Label, StringComparison.Ordinal);
        Assert.Contains("process", summary.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionVisualizerFactory_AgentSessionStop_ReturnsSummary()
    {
        var factory = new AgentSessionVisualizerFactory();
        var call = new FunctionCallContent("call-1", "agent_session_stop",
            new Dictionary<string, object?>
            {
                ["session_id"] = "session123",
                ["dispose"] = true,
            });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        var summary = Assert.IsType<Summary>(result);
        Assert.Contains("stopped", summary.Label, StringComparison.Ordinal);
        Assert.Contains("disposed", summary.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionVisualizerFactory_AgentSessionWait_ReturnsSummary()
    {
        var factory = new AgentSessionVisualizerFactory();
        var call = new FunctionCallContent("call-1", "agent_session_wait",
            new Dictionary<string, object?>
            {
                ["session_id"] = "session456",
                ["timeout_seconds"] = 30,
            });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        var summary = Assert.IsType<Summary>(result);
        Assert.Contains("waiting", summary.Label, StringComparison.Ordinal);
        Assert.Contains("30s", summary.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionVisualizerFactory_UnrelatedTool_ReturnsNull()
    {
        var factory = new AgentSessionVisualizerFactory();
        var call = new FunctionCallContent("call-1", "some_other_tool",
            new Dictionary<string, object?> { ["param"] = "value" });

        var result = factory.Visualize(new ToolVisualizationContext(call));

        Assert.Null(result);
    }

    [Fact]
    public void AgentSessionVisualizerFactory_FunctionResult_ReturnsNull()
    {
        var factory = new AgentSessionVisualizerFactory();
        var result = new FunctionResultContent("call-1", "session created");

        var visualization = factory.Visualize(new ToolVisualizationContext(result));

        Assert.Null(visualization);
    }

    // --- AgentChatStatusLineViewModel ---

    [Fact]
    public async Task AgentChatStatusLineViewModel_UpdateStatus_SetsIntentDisplay()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agent = new AgentViewModel(CreateChat(), "test", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agent);

        statusLine.UpdateStatus(AgentStatusField.Intent, "doing a thing");

        Assert.Equal("doing a thing", statusLine.IntentDisplay);
    }

    [Fact]
    public async Task AgentChatStatusLineViewModel_IntentDisplay_ClearedWhenThinkingStops()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agent = new AgentViewModel(CreateChat(), "test", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agent);

        var runningItem = agent.AgentChat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new TextContent("thinking")],
        });

        statusLine.UpdateStatus(AgentStatusField.Intent, "searching files");
        Assert.Equal("searching files", statusLine.IntentDisplay);

        agent.AgentChat.CompleteRunningItem(runningItem, writeToHistory: false);

        Assert.Null(statusLine.IntentDisplay);
    }

    [Fact]
    public async Task AgentChatStatusLineViewModel_UpdateStatus_EmptyValue_ClearsDisplay()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agent = new AgentViewModel(CreateChat(), "test", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agent);

        statusLine.UpdateStatus(AgentStatusField.Intent, "something");
        statusLine.UpdateStatus(AgentStatusField.Intent, string.Empty);

        Assert.Null(statusLine.IntentDisplay);
    }

    // --- Inline inspector affordance removed (#293) ---

    [Fact]
    public void ChatOutputHtmlRenderer_FunctionCallBlock_DoesNotContainInspectorAffordance()
    {
        var call = new FunctionCallContent("call-1", "my_tool",
            new Dictionary<string, object?> { ["arg"] = "val" });

        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain("chat-inspect", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputHtmlRenderer_FunctionResultBlock_DoesNotContainInspectorAffordance()
    {
        var result = new FunctionResultContent("call-1", "result value");

        var html = ChatOutputHtmlRenderer.RenderContent("c0", result, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain("chat-inspect", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputHtmlRenderer_TextBlock_DoesNotContainInspectorAffordance()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("hello"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain("chat-inspect", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputHtmlRenderer_WithFactory_ReportIntent_ProducesEmptyOutput()
    {
        var factory = new CopilotToolVisualizerFactory();
        var sink = new RecordingStatusSink();
        var call = new FunctionCallContent("call-1", "report_intent",
            new Dictionary<string, object?> { ["intent"] = "test intent" });

        var html = ChatOutputHtmlRenderer.RenderContent("c0", call,
            includeReasoning: false, isDiagnostic: false,
            toolFactory: factory, statusSink: sink);

        Assert.Equal(string.Empty, html);
        Assert.Single(sink.Updates);
        Assert.Equal("test intent", sink.Updates[0].Value);
    }

    [Fact]
    public void ChatOutputHtmlRenderer_WithFactory_Summary_RendersExpandedDetails()
    {
        var factory = new StubFactory(new Summary("workspace call", "<b>details</b>"));
        var call = new FunctionCallContent("call-1", "any_tool", new Dictionary<string, object?>());

        var html = ChatOutputHtmlRenderer.RenderContent("c0", call,
            includeReasoning: false, isDiagnostic: false,
            toolFactory: factory, statusSink: null);

        Assert.NotNull(html);
        Assert.Contains("open", html, StringComparison.Ordinal);
        Assert.Contains("workspace call", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatOutputHtmlRenderer_WithFactory_NullResult_FallsBackToGenericCollapsible()
    {
        var factory = new StubFactory(null);
        var call = new FunctionCallContent("call-1", "my_tool", new Dictionary<string, object?> { ["x"] = "y" });

        var html = ChatOutputHtmlRenderer.RenderContent("c0", call,
            includeReasoning: false, isDiagnostic: false,
            toolFactory: factory, statusSink: null);

        Assert.NotNull(html);
        Assert.Contains("tool call:", html, StringComparison.Ordinal);
        Assert.Contains("<details", html, StringComparison.Ordinal);
    }

    // --- Helpers ---

    private static AgentChat CreateChat()
    {
        var requestType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest type was not found.");
        var request = Activator.CreateInstance(requestType)
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest could not be created.");
        var configuredStoreProperty = requestType.GetProperty("ConfiguredStore")
            ?? throw new InvalidOperationException("ConfiguredStore property was not found.");
        configuredStoreProperty.SetValue(request, new InMemoryAgentPersistenceStore());

        var constructor = typeof(AgentChat).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [requestType],
            modifiers: null)
            ?? throw new InvalidOperationException("AgentChat constructor was not found.");

        return (AgentChat)constructor.Invoke([request]);
    }

    private sealed class StubFactory(object? returnValue) : IToolVisualizerFactory
    {
        public object? Visualize(ToolVisualizationContext context) => returnValue;
    }

    private sealed class RecordingStatusSink : IAgentStatusSink
    {
        public List<(AgentStatusField Field, string? Value)> Updates { get; } = [];

        public void UpdateStatus(AgentStatusField field, string? value)
            => this.Updates.Add((field, value));
    }
}

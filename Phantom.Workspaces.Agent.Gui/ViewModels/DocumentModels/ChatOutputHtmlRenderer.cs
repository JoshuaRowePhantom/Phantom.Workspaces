using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal enum StringContentType
{
    Json,
    Markdown,
    Code,
    Plaintext,
}

/// <summary>
/// Pure HTML generation for chat output. Produces the DOM shape consumed by the browser-hosted
/// renderer (<c>div.chat-message</c> &gt; <c>div.chat-header</c> + <c>div.chat-contents</c> &gt;
/// <c>div.chat-content</c>). Assistant/user text is rendered from Markdown to HTML (with raw HTML
/// disabled); all other dynamic text is HTML-escaped. Stateless and fully testable.
/// </summary>
internal static class ChatOutputHtmlRenderer
{
    public const string HistoryContainerId = "chat-history-container";
    public const string RunningContainerId = "running-items-container";
    public const string SubAgentPanelSentinelId = "subagent-panel-sentinel";
    public const string SubAgentPanelInnerId = "subagent-panel-inner";
    public const string ParentAgentPanelSentinelId = "parent-agent-panel-sentinel";
    public const string ParentAgentPanelInnerId = "parent-agent-panel-inner";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    // Tool RESULT display caps (issue #1069). A result whose payload exceeds either threshold is
    // collapsed to a short "(N lines)" / "(N characters)" summary instead of dumping the entire
    // escaped one-liner into the summary and the fully-expanded tree into the body. The full
    // payload remains available for inspection via the data-details-target attribute.
    internal const int MaxToolResultLines = 20;
    internal const int MaxToolResultCharacters = 2000;

    // Assistant/user text is authored in Markdown. Raw HTML pass-through is disabled so any literal
    // angle brackets in the model output are escaped (the rendered HTML is injected into the chat
    // WebView, where un-escaped markup would be an injection risk).
    // UsePipeTables enables GitHub-Flavored Markdown pipe tables (| col | col |).
    // UseAutoLinks makes bare https:// and http:// URLs in plain text clickable.
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public static string MessageId(int historyIndex) => $"history-{historyIndex}";

    public static string RunningMessageId(string runningItemId, int localIndex) => $"{runningItemId}-msg-{localIndex}";

    public static string HeaderId(string messageId) => $"{messageId}-header";

    public static string ContentsContainerId(string messageId) => $"{messageId}-contents";

    public static string ContentId(string messageId, int subIndex) => $"{messageId}-{subIndex}";

    public static string RunningItemId(int sequence) => $"run-{sequence}";

    public static string RunningItemContentsId(string runningItemId) => $"{runningItemId}-contents";

    public static string ToolGroupId(int firstHistoryIndex) => $"tool-group-{firstHistoryIndex}";

    public static string ToolGroupSummaryId(string groupId) => $"{groupId}-summary";

    public static string ToolGroupBodyId(string groupId) => $"{groupId}-body";

    /// <summary>
    /// Builds the outer <c>details.chat-tool-group</c> element that groups a run of consecutive
    /// tool-call messages. <paramref name="bodyContent"/> is the pre-rendered HTML of the first
    /// message and is placed directly inside the body container.
    /// <paramref name="toolNames"/> is the deduped, first-seen-order list of tool names in the group.
    /// </summary>
    public static string RenderToolCallGroup(string groupId, IReadOnlyList<string> toolNames, int callCount, string bodyContent)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool-group\" id=\"").Append(groupId).Append("\">");
        builder.Append(RenderToolCallGroupSummary(groupId, toolNames, callCount));
        builder.Append("<div class=\"chat-tool-group-body\" id=\"").Append(ToolGroupBodyId(groupId)).Append("\">");
        builder.Append(bodyContent);
        builder.Append("</div></details>");
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>summary</c> element for a tool-call group. Always lists the unique tool names
    /// in first-seen order, formatted <c>tools (a, b)</c> (a single tool renders <c>tools (a)</c>),
    /// followed by the call-count badge.
    /// </summary>
    public static string RenderToolCallGroupSummary(string groupId, IReadOnlyList<string> toolNames, int callCount)
    {
        var builder = new StringBuilder();
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"2\" id=\"").Append(ToolGroupSummaryId(groupId)).Append("\">");

        if (toolNames is { Count: > 0 })
        {
            builder.Append("tools (");
            for (var i = 0; i < toolNames.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("<span class=\"tool-name\">").Append(HtmlEscape(toolNames[i])).Append("</span>");
            }

            builder.Append(')');
        }
        else
        {
            builder.Append("tools");
        }

        builder.Append(" <span class=\"tool-count-badge\">").Append(callCount).Append(" calls</span>");
        builder.Append("<button type=\"button\" class=\"tool-expand-toggle\" data-tool-expand-toggle ")
            .Append("aria-label=\"Expand or collapse all tools\" aria-hidden=\"true\">")
            .Append("\u21F2</button>");
        builder.Append("</summary>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders the outer "tools (N calls)" wrapper used only when there are more than one call in a
    /// group. The <paramref name="summary"/> is a pre-formatted one-liner such as
    /// <c>last_tool(…)</c>. <paramref name="innerHtml"/> is the pre-rendered set of
    /// <c>details.chat-tool-group-item</c> elements placed directly inside.
    /// </summary>
    public static string RenderToolGroupWrapper(string contentId, int callCount, string summary, string innerHtml)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool-group-wrapper\" id=\"").Append(contentId).Append("\">");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"2\">tools  ").Append(HtmlEscape(summary))
            .Append("  (").Append(callCount).Append(" calls)")
            .Append("<button type=\"button\" class=\"tool-expand-toggle\" data-tool-expand-toggle ")
            .Append("aria-label=\"Expand or collapse all tools\" aria-hidden=\"true\">")
            .Append("\u21F2</button>")
            .Append("</summary>");
        builder.Append(innerHtml);
        builder.Append("</details>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders one "tool &lt;name&gt;" row as a <c>details.chat-tool-group-item</c> element
    /// containing call and optional result sub-details. When <paramref name="contentId"/> is
    /// non-null the outer element gets that value as its <c>id</c> attribute (used for the
    /// standalone N=1 case where the element must be DOM-addressable). Pass
    /// <see langword="null"/> for items nested inside a <see cref="RenderToolGroupWrapper"/>.
    /// </summary>
    public static string RenderToolCallPair(
        string? contentId,
        string name,
        string callJson,
        string? resultJson,
        string? callDetailsJson = null,
        string? resultDetailsJson = null,
        string? memberIdBase = null)
    {
        var callSummary = HtmlEscape(name) + "(…)";
        var idBase = memberIdBase ?? contentId;
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool-group-item\"");
        if (!string.IsNullOrEmpty(contentId))
        {
            builder.Append(" id=\"").Append(contentId).Append("\"");
        }

        builder.Append(">");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"3\">tool ").Append(callSummary).Append("</summary>");

        // Tool-CALL block — gutter host (copy + inspect). The "..." details-gutter was removed (#1038),
        // so reusing data-details-target for the inspect payload is safe (#1039).
        builder.Append("<details class=\"chat-tool-call\" data-copy-target data-inspect-target");
        if (!string.IsNullOrEmpty(callDetailsJson))
        {
            builder.Append(" data-details-target=\"").Append(HtmlEscape(callDetailsJson)).Append("\"");
        }

        if (!string.IsNullOrEmpty(idBase))
        {
            builder.Append(" id=\"").Append(idBase).Append("-call\"");
        }

        builder.Append(" open>");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"4\">call  ").Append(callSummary).Append("</summary>");
        if (!string.IsNullOrEmpty(callJson))
        {
            builder.Append("<pre class=\"chat-collapsible-body tool-json-value\">").Append(RenderToolPayload(callJson)).Append("</pre>");
        }

        builder.Append("</details>");

        if (resultJson is not null)
        {
            var resultSummary = SummarizeResult(resultJson);

            // Tool-RESULT block — gutter host (copy + inspect).
            builder.Append("<details class=\"chat-tool-result\" data-copy-target data-inspect-target");
            if (!string.IsNullOrEmpty(resultDetailsJson))
            {
                builder.Append(" data-details-target=\"").Append(HtmlEscape(resultDetailsJson)).Append("\"");
            }

            if (!string.IsNullOrEmpty(idBase))
            {
                builder.Append(" id=\"").Append(idBase).Append("-result\"");
            }

            builder.Append(" open>");
            builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"4\">result  ").Append(HtmlEscape(resultSummary)).Append("</summary>");
            if (!string.IsNullOrEmpty(resultJson))
            {
                var (bodyHtml, _) = RenderToolResultBody(resultJson);
                builder.Append("<pre class=\"chat-collapsible-body tool-json-value\">").Append(bodyHtml).Append("</pre>");
            }

            builder.Append("</details>");
        }

        builder.Append("</details>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders a content-level tool group for a run of consecutive
    /// <see cref="FunctionCallContent"/> items, pairing each with its matching
    /// <see cref="FunctionResultContent"/> from <paramref name="resultLookup"/> (keyed by
    /// <c>CallId</c>). When the group has exactly one call the outer wrapper is omitted and
    /// the single <c>details.chat-tool-group-item</c> is returned with <paramref name="contentId"/>
    /// as its DOM id. When there are multiple calls the result is a
    /// <c>details.chat-tool-group-wrapper</c> containing all pairs.
    /// </summary>
    public static string RenderToolGroup(
        string contentId,
        IReadOnlyList<FunctionCallContent> calls,
        IReadOnlyDictionary<string, FunctionResultContent>? resultLookup)
    {
        if (calls.Count == 1)
        {
            var call = calls[0];
            FunctionResultContent? result = null;
            if (call.CallId is not null)
            {
                resultLookup?.TryGetValue(call.CallId, out result);
            }

            return RenderToolCallPair(
                contentId,
                call.Name ?? string.Empty,
                PrettyJson(call.Arguments),
                result is not null ? PrettyJson(result.Result) : null,
                SerializeContentJson(call),
                result is not null ? SerializeContentJson(result) : null);
        }
        else
        {
            var innerBuilder = new StringBuilder();
            var lastCallName = string.Empty;
            var memberIndex = 0;
            foreach (var call in calls)
            {
                FunctionResultContent? result = null;
                if (call.CallId is not null)
                {
                    resultLookup?.TryGetValue(call.CallId, out result);
                }

                innerBuilder.Append(RenderToolCallPair(
                    null,
                    call.Name ?? string.Empty,
                    PrettyJson(call.Arguments),
                    result is not null ? PrettyJson(result.Result) : null,
                    SerializeContentJson(call),
                    result is not null ? SerializeContentJson(result) : null,
                    $"{contentId}-{memberIndex}"));
                lastCallName = call.Name ?? string.Empty;
                memberIndex++;
            }

            return RenderToolGroupWrapper(contentId, calls.Count, lastCallName + "(…)", innerBuilder.ToString());
        }
    }

    /// <summary>Builds the full <c>div.chat-message</c> element for a message and its visible contents.</summary>
    public static string RenderMessage(
        string messageId,
        string roleLabel,
        IReadOnlyList<(string ElementId, string Html)> contents,
        DateTimeOffset? timestamp = null,
        string? jumpLinkHtml = null)
    {
        var builder = new StringBuilder();
        builder.Append("<div class=\"chat-message ").Append(RoleClass(roleLabel)).Append("\" id=\"")
            .Append(messageId).Append("\" data-sticky-base-level=\"0\">");
        builder.Append(RenderHeader(messageId, roleLabel, timestamp));
        builder.Append("<div class=\"chat-contents\" id=\"").Append(ContentsContainerId(messageId)).Append("\">");
        foreach (var content in contents)
        {
            builder.Append(content.Html);
        }

        builder.Append("</div>");
        if (jumpLinkHtml is not null)
        {
            builder.Append(jumpLinkHtml);
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders the '→ Open sub-agent' jump link element for a tool-result message whose content
    /// carries a parent tool-call id that maps to a running sub-agent. Clicking the element posts
    /// <c>{ type: "navigateToAgent", agentId }</c> to the host.
    /// </summary>
    public static string RenderSubAgentJumpLink(string agentId)
    {
        var builder = new StringBuilder();
        builder.Append("<div class=\"chat-subagent-jump\">");
        builder.Append("<button class=\"chat-jump-link\" data-navigate-agent-id=\"")
            .Append(HtmlEscape(agentId)).Append("\">");
        builder.Append("→ Open sub-agent");
        builder.Append("</button></div>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders a breadcrumb bar of ancestor links for a sub-agent view, root-first. Each entry is a
    /// <c>chat-jump-link</c> button carrying <c>data-navigate-agent-id</c> so the existing JS click
    /// handler dispatches navigation. Returns <see cref="string.Empty"/> when <paramref name="ancestors"/>
    /// is empty (i.e. root agent — no ancestors to display).
    /// </summary>
    public static string RenderAncestorLinks(IReadOnlyList<AncestorLinkHtmlModel> ancestors)
    {
        if (ancestors.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"chat-ancestor-breadcrumb\">");
        for (var i = 0; i < ancestors.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" &rsaquo; ");
            }

            var a = ancestors[i];
            var label = a.IsRoot ? a.DisplayName + " (root)"
                : a.IsCurrent ? a.DisplayName + " (current)"
                : a.DisplayName;
            sb.Append("<button class=\"chat-jump-link\" data-navigate-agent-id=\"")
                .Append(HtmlEscape(a.AgentId)).Append("\">")
                .Append(HtmlEscape(label)).Append("</button>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }


    public static string RenderRunningItemContainer(string runningItemId)
        => $"<div class=\"chat-running-item\" id=\"{runningItemId}\"><div class=\"chat-running-contents\" id=\"{RunningItemContentsId(runningItemId)}\"></div></div>";

    /// <summary>
    /// Returns an empty string for the <c>tool</c> role — results are bundled into the assistant
    /// message's tool-group hierarchy and need no separate role header.
    /// </summary>
    public static string RenderHeader(string messageId, string roleLabel, DateTimeOffset? timestamp = null)
    {
        if (string.Equals(roleLabel, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<div class=\"chat-header\" id=\"").Append(HeaderId(messageId)).Append("\" data-sticky-level=\"1\">");
        builder.Append("<span>[").Append(HtmlEscape(roleLabel)).Append("]</span>");
        if (timestamp.HasValue)
        {
            builder.Append("<span class=\"chat-timestamp\" data-utc=\"")
                .Append(timestamp.Value.ToString("O"))
                .Append("\"></span>");
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    public static string RoleClass(string roleLabel)
    {
        if (string.Equals(roleLabel, "user", StringComparison.OrdinalIgnoreCase))
        {
            return "chat-user-message";
        }
        if (string.Equals(roleLabel, "help", StringComparison.OrdinalIgnoreCase))
        {
            return "chat-help-message";
        }
        return "chat-assistant-message";
    }

    /// <summary>
    /// Renders a single content block to a <c>div.chat-content</c> (or collapsible <c>details</c>)
    /// element, or returns <see langword="null"/> when the block should not be shown (hidden reasoning
    /// or empty text).
    /// </summary>
    public static string? RenderContent(
        string contentId,
        AIContent content,
        bool includeReasoning,
        bool isDiagnostic,
        bool isHelp = false,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        switch (content)
        {
            case TextReasoningContent reasoning:
                if (!includeReasoning || string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    return null;
                }

                return TextBlock(contentId, "chat-reasoning", reasoning.Text, SerializeContentJson(reasoning));
            case TextContent text when isHelp && !string.IsNullOrWhiteSpace(text.Text):
                return TextBlock(contentId, "chat-help", text.Text, SerializeContentJson(text));
            case TextContent text when isDiagnostic && !string.IsNullOrWhiteSpace(text.Text):
                return RenderCollapsible(contentId, "chat-diagnostic", DiagnosticHeader(text.Text), DiagnosticBody(text.Text), SerializeContentJson(text));
            case TextContent text:
                return string.IsNullOrWhiteSpace(text.Text) ? null : MarkdownBlock(contentId, "chat-text", text.Text, SerializeContentJson(text));
            case FunctionCallContent call:
            {
                if (toolFactory is not null)
                {
                    var context = new ToolVisualizationContext(call);
                    var factoryResult = toolFactory.Visualize(context);
                    var interpreted = ToolVisualizationInterpreter.Interpret(factoryResult, contentId, statusSink);
                    if (interpreted is not null)
                    {
                        return string.IsNullOrEmpty(interpreted) ? string.Empty : interpreted;
                    }
                }

                var description = call.Arguments is not null && call.Arguments.TryGetValue("description", out var descObj) ? descObj as string : null;
                var label = string.IsNullOrEmpty(description) ? $"tool call: {call.Name}" : $"tool call: {call.Name}: {description}";
                return RenderCollapsible(contentId, "chat-tool", label, RenderToolPayload(call.Arguments), SerializeContentJson(call), bodyIsHtml: true);
            }

            case FunctionResultContent result:
            {
                if (toolFactory is not null)
                {
                    var context = new ToolVisualizationContext(result);
                    var factoryResult = toolFactory.Visualize(context);
                    var interpreted = ToolVisualizationInterpreter.Interpret(factoryResult, contentId, statusSink);
                    if (interpreted is not null)
                    {
                        return string.IsNullOrEmpty(interpreted) ? string.Empty : interpreted;
                    }
                }

                return RenderCollapsible(contentId, "chat-tool", $"tool result: {result.CallId}", RenderToolPayload(result.Result), SerializeContentJson(result), bodyIsHtml: true);
            }

            case DataContent data:
                return IsImageMediaType(data.MediaType)
                    ? TextBlock(contentId, "chat-meta", string.IsNullOrWhiteSpace(data.MediaType) ? "image" : data.MediaType, SerializeContentJson(data))
                    : TextBlock(contentId, "chat-monospace", string.IsNullOrWhiteSpace(data.MediaType) ? "[data]" : $"[{data.MediaType}]", SerializeContentJson(data));
            case ErrorContent error:
                return TextBlock(contentId, "chat-error", error.Message ?? string.Empty, SerializeContentJson(error));
            case UriContent uri:
                return TextBlock(contentId, "chat-uri", uri.Uri.ToString(), SerializeContentJson(uri));
            default:
                return TextBlock(contentId, "chat-text", content.ToString() ?? string.Empty, SerializeContentJson(content));
        }
    }

    /// <summary>
    /// A cheap value-based identity for a content block, excluding large immutable payloads (tool
    /// arguments, tool results, binary data) so comparing it on every streaming update stays cheap.
    /// </summary>
    public static string ComputeContentKey(AIContent content, bool isDiagnostic)
    {
        return content switch
        {
            TextReasoningContent reasoning => "reasoning:" + reasoning.Text,
            TextContent text when isDiagnostic => "diagnostic:" + text.Text,
            TextContent text => "text:" + text.Text,
            FunctionCallContent call => $"call:{call.CallId}\u0001{call.Name}\u0001{call.Arguments?.Count ?? -1}",
            FunctionResultContent result => "result:" + result.CallId,
            DataContent data => $"data:{data.MediaType}\u0001{data.Data.Length}",
            ErrorContent error => "error:" + error.Message,
            UriContent uri => "uri:" + uri.Uri,
            _ => $"other:{content.GetType().FullName}\u0001{content}",
        };
    }

    public static string HtmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    internal static StringContentType DetectStringType(string value)
    {
        var trimmed = value.TrimStart();
        if ((trimmed.StartsWith('{') || trimmed.StartsWith('['))
            && TryParseJson(value, out _))
        {
            return StringContentType.Json;
        }

        if (trimmed.Contains("## ", StringComparison.Ordinal)
            || trimmed.Contains("**", StringComparison.Ordinal)
            || trimmed.Contains("```", StringComparison.Ordinal)
            || trimmed.Contains("\n- ", StringComparison.Ordinal)
            || trimmed.Contains("\n> ", StringComparison.Ordinal)
            || trimmed.Contains("---", StringComparison.Ordinal))
        {
            return StringContentType.Markdown;
        }

        if (value.Contains('\n')
            && (value.Contains("    ", StringComparison.Ordinal)
                || value.Contains('\t')
                || value.Contains('{', StringComparison.Ordinal)
                || value.Contains("=>", StringComparison.Ordinal)
                || value.Contains("return ", StringComparison.Ordinal)
                || value.Contains("def ", StringComparison.Ordinal)))
        {
            return StringContentType.Code;
        }

        return StringContentType.Plaintext;
    }

    internal static string RenderJsonValue(JsonElement value, int indentLevel)
        => RenderJsonValue(value, indentLevel, continuationIndent: indentLevel * 2);

    private static string RenderJsonValue(JsonElement value, int indentLevel, int continuationIndent)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => RenderJsonObject(value, indentLevel),
            JsonValueKind.Array => RenderJsonArray(value, indentLevel),
            JsonValueKind.String => RenderStringValue(value.GetString() ?? string.Empty, indentLevel, continuationIndent),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => HtmlEscape(value.ToString()),
            JsonValueKind.Null => "null",
            _ => HtmlEscape(value.ToString()),
        };
    }

    private static string RenderJsonObject(JsonElement obj, int indentLevel)
    {
        var properties = obj.EnumerateObject().ToList();
        if (properties.Count == 0)
        {
            return "{}";
        }

        var indent = new string(' ', indentLevel * 2);
        var maxKeyLength = properties.Max(property => property.Name.Length);
        var valueColumn = indent.Length + maxKeyLength + 2;
        var builder = new StringBuilder();

        foreach (var property in properties)
        {
            builder.Append(indent);
            builder.Append("<span class=\"tool-json-key\">")
                .Append(HtmlEscape(property.Name.PadRight(maxKeyLength)))
                .Append("</span>: ");

            var renderedValue = RenderJsonValue(property.Value, indentLevel + 1, valueColumn);
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                && renderedValue.Length > 0
                && renderedValue[0] != '\n')
            {
                builder.Append('\n');
            }

            builder.Append(renderedValue);
            if (!renderedValue.EndsWith('\n'))
            {
                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string RenderJsonArray(JsonElement array, int indentLevel)
    {
        var values = array.EnumerateArray().ToList();
        if (values.Count == 0)
        {
            return "[]";
        }

        var indent = new string(' ', indentLevel * 2);
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append(indent).Append("- ");
            var renderedValue = RenderJsonValue(value, indentLevel + 1, indent.Length + 2);
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                && renderedValue.Length > 0
                && renderedValue[0] != '\n')
            {
                builder.Append('\n');
            }

            builder.Append(renderedValue);
            if (!renderedValue.EndsWith('\n'))
            {
                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string RenderStringValue(string value, int indentLevel, int continuationIndent)
    {
        switch (DetectStringType(value))
        {
            case StringContentType.Json:
                using (var document = JsonDocument.Parse(value))
                {
                    return RenderJsonValue(document.RootElement, indentLevel, continuationIndent);
                }
            case StringContentType.Markdown:
                return "<span class=\"tool-json-markdown\">" + MarkdownToHtml(value) + "</span>";
            case StringContentType.Code:
                return "<code class=\"tool-json-code\">" + HtmlEscape(value) + "</code>";
            default:
                return "<span class=\"tool-json-plaintext\">" + RenderPlaintextWithContinuation(value, continuationIndent) + "</span>";
        }
    }

    private static string RenderPlaintextWithContinuation(string value, int continuationIndent)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 1)
        {
            return HtmlEscape(value);
        }

        var builder = new StringBuilder();
        builder.Append(HtmlEscape(lines[0]));
        var continuation = new string(' ', continuationIndent);
        for (var i = 1; i < lines.Length; i++)
        {
            builder.Append('\n').Append(continuation).Append(HtmlEscape(lines[i]));
        }

        return builder.ToString();
    }

    private static string TextBlock(string contentId, string cssClass, string text, string detailsJson)
        => $"<div class=\"chat-content {cssClass}\" data-copy-target data-details-target=\"{HtmlEscape(detailsJson)}\" data-inspect-target id=\"{contentId}\">{HtmlEscape(text)}</div>";

    /// <summary>
    /// Renders Markdown text into a <c>div.chat-content</c> container. The Markdown is converted to
    /// block-level HTML (headings, paragraphs, lists, fenced code, blockquotes, inline emphasis/code)
    /// with raw HTML disabled so model output cannot inject markup into the WebView.
    /// </summary>
    private static string MarkdownBlock(string contentId, string cssClass, string text, string detailsJson)
        => $"<div class=\"chat-content {cssClass}\" data-copy-target data-details-target=\"{HtmlEscape(detailsJson)}\" data-inspect-target id=\"{contentId}\">{MarkdownToHtml(text)}</div>";

    private static string MarkdownToHtml(string text)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        MarkdownPipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<LinkInlineRenderer>(new ExternalLinkInlineRenderer());
        var document = Markdown.Parse(text, MarkdownPipeline);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString().TrimEnd('\n', '\r');
    }

    private static string RenderCollapsible(string contentId, string cssClass, string header, string body, string detailsJson, bool bodyIsHtml = false)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content ").Append(cssClass).Append("\" data-copy-target data-details-target=\"").Append(HtmlEscape(detailsJson)).Append("\" data-inspect-target data-sticky-base-level=\"1\" id=\"").Append(contentId).Append("\">");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"0\">").Append(HtmlEscape(header)).Append("</summary>");
        if (!string.IsNullOrEmpty(body))
        {
            builder.Append("<pre class=\"chat-collapsible-body");
            if (bodyIsHtml)
            {
                builder.Append(" tool-json-value");
            }

            builder.Append("\">").Append(bodyIsHtml ? body : HtmlEscape(body)).Append("</pre>");
        }

        builder.Append("</details>");
        return builder.ToString();
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.TrimEnd();
        var newlineIdx = trimmed.IndexOf('\n');
        return newlineIdx >= 0 ? trimmed[..newlineIdx].TrimEnd('\r') : trimmed;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static bool ToolResultOverflows(string resultJson)
        => resultJson.Length > MaxToolResultCharacters || CountLines(resultJson) > MaxToolResultLines;

    private static string ToolResultOverflowSummary(string resultJson)
    {
        var lineCount = CountLines(resultJson);
        return lineCount > 1
            ? $"({lineCount} lines)"
            : $"({resultJson.Length} characters)";
    }

    // Summary line for the tool RESULT block. Small results show their real first line; oversized
    // results collapse to "(N lines)" / "(N characters)" so the escaped one-liner is never dumped
    // verbatim into the summary (issue #1069).
    private static string SummarizeResult(string resultJson)
        => ToolResultOverflows(resultJson)
            ? ToolResultOverflowSummary(resultJson)
            : FirstLine(resultJson);

    // Body for the tool RESULT block. Small results render in full; oversized results render only
    // the short "(N lines)" / "(N characters)" summary instead of the fully-expanded payload tree
    // (issue #1069). The full payload stays available via data-details-target.
    private static (string Html, bool Overflowed) RenderToolResultBody(string resultJson)
        => ToolResultOverflows(resultJson)
            ? (HtmlEscape(ToolResultOverflowSummary(resultJson)), true)
            : (RenderToolPayload(resultJson), false);

    private static string DiagnosticHeader(string text)
    {
        var trimmed = text.TrimEnd();
        var newlineIndex = trimmed.IndexOf('\n');
        return newlineIndex >= 0 ? trimmed[..newlineIndex].TrimEnd('\r') : trimmed;
    }

    private static string DiagnosticBody(string text)
    {
        var trimmed = text.TrimEnd();
        var newlineIndex = trimmed.IndexOf('\n');
        return newlineIndex >= 0 ? trimmed[(newlineIndex + 1)..] : string.Empty;
    }

    private static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string SerializeContentJson(AIContent content)
    {
        try
        {
            return JsonSerializer.Serialize(content, PrettyJsonOptions);
        }
        catch
        {
            return content.ToString() ?? string.Empty;
        }
    }

    private static string PrettyJson(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return TryPrettyPrintJson(text, out var prettyText) ? prettyText : text;
            case JsonElement element:
                return JsonSerializer.Serialize(element, PrettyJsonOptions);
            default:
                try
                {
                    return JsonSerializer.Serialize(value, PrettyJsonOptions);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    return value.ToString() ?? string.Empty;
                }
        }
    }

    private static string RenderToolPayload(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        // Fallback renderer: a pathological tool payload (non-serializable / cyclic arguments,
        // malformed markdown, etc.) must never abort chat rendering. Any non-fatal failure falls
        // back to escaped text so history and live output always render.
        try
        {
            switch (value)
            {
                case string text:
                    if (TryParseJson(text, out var document))
                    {
                        using (document)
                        {
                            return RenderJsonValue(document!.RootElement, 0);
                        }
                    }

                    return RenderStringValue(text, 0, 0);
                case JsonElement element:
                    return RenderJsonValue(element, 0);
                default:
                    var serialized = JsonSerializer.SerializeToElement(value, PrettyJsonOptions);
                    return RenderJsonValue(serialized, 0);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return HtmlEscape(value.ToString() ?? string.Empty);
        }
    }

    private static bool TryPrettyPrintJson(string text, out string pretty)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            pretty = string.Empty;
            return false;
        }
    }

    private static bool TryParseJson(string text, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    /// <summary>
    /// Adds <c>target="_blank"</c> and <c>rel="noopener noreferrer"</c> to any link whose URL
    /// starts with <c>http://</c> or <c>https://</c>. Used for both auto-linked bare URLs and
    /// explicit Markdown link syntax.
    /// </summary>
    private sealed class ExternalLinkInlineRenderer : LinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            if (!link.IsImage)
            {
                var url = link.Url ?? string.Empty;
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
                    this.Rel = "noopener noreferrer";
                }
            }

            base.Write(renderer, link);
        }
    }
}

/// <summary>
/// Immutable model for a single ancestor entry in the breadcrumb bar.
/// </summary>
internal sealed record AncestorLinkHtmlModel(string AgentId, string DisplayName, bool IsRoot, bool IsCurrent);

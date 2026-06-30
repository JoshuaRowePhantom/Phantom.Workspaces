using System;
using System.IO;
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

/// <summary>
/// Pure HTML generation for chat output. Produces the DOM shape consumed by the browser-hosted
/// renderer (<c>div.chat-message</c> &gt; <c>div.chat-header</c> + <c>div.chat-contents</c> &gt;
/// <c>div.chat-content</c>). Assistant/user text is rendered from Markdown to HTML (with raw HTML
/// disabled); all other dynamic text is HTML-escaped. Stateless and fully testable.
/// </summary>
internal static class ChatOutputHtmlRenderer
{
    public const string HistoryContainerId = "chat-history";
    public const string RunningContainerId = "chat-running";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

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

    public static string MessageId(int sequence) => $"msg-{sequence}";

    public static string HeaderId(string messageId) => $"{messageId}-header";

    public static string ContentsContainerId(string messageId) => $"{messageId}-contents";

    public static string ContentId(string messageId, int index) => $"{messageId}-c{index}";

    public static string RunningItemId(int sequence) => $"run-{sequence}";

    public static string RunningItemContentsId(string runningItemId) => $"{runningItemId}-contents";

    public static string ToolCallGroupId(int sequence) => $"grp-{sequence}";

    public static string ToolCallGroupSummaryId(string groupId) => $"{groupId}-summary";

    public static string ToolCallGroupBodyId(string groupId) => $"{groupId}-body";

    /// <summary>
    /// Builds the outer <c>details.chat-tool-group</c> element that groups a run of consecutive
    /// tool-call messages. <paramref name="bodyContent"/> is the pre-rendered HTML of the first
    /// message and is placed directly inside the body container.
    /// </summary>
    public static string RenderToolCallGroup(string groupId, string lastToolName, int callCount, string bodyContent)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool-group\" id=\"").Append(groupId).Append("\">");
        builder.Append(RenderToolCallGroupSummary(groupId, lastToolName, callCount));
        builder.Append("<div class=\"chat-tool-group-body\" id=\"").Append(ToolCallGroupBodyId(groupId)).Append("\">");
        builder.Append(bodyContent);
        builder.Append("</div></details>");
        return builder.ToString();
    }

    /// <summary>Builds the <c>summary</c> element for an existing tool-call group (used when the group is extended).</summary>
    public static string RenderToolCallGroupSummary(string groupId, string lastToolName, int callCount)
    {
        var builder = new StringBuilder();
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"2\" id=\"").Append(ToolCallGroupSummaryId(groupId)).Append("\">");
        builder.Append("tool call: <span class=\"tool-name\">").Append(HtmlEscape(lastToolName)).Append("</span>");
        builder.Append(" <span class=\"tool-count-badge\">").Append(callCount).Append(" calls</span>");
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
            .Append("  (").Append(callCount).Append(" calls)</summary>");
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
        string? resultJson)
    {
        var callSummary = HtmlEscape(name) + "(…)";
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool-group-item\"");
        if (!string.IsNullOrEmpty(contentId))
        {
            builder.Append(" id=\"").Append(contentId).Append("\"");
        }

        builder.Append(">");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"3\">tool ").Append(callSummary).Append("</summary>");

        builder.Append("<details class=\"chat-tool-call\" open>");
        builder.Append("<summary class=\"chat-collapsible-summary\">call  ").Append(callSummary).Append("</summary>");
        if (!string.IsNullOrEmpty(callJson))
        {
            builder.Append("<pre class=\"chat-collapsible-body\">").Append(HtmlEscape(callJson)).Append("</pre>");
        }

        builder.Append("</details>");

        if (resultJson is not null)
        {
            var resultSummary = FirstLine(resultJson);
            builder.Append("<details class=\"chat-tool-result\" open>");
            builder.Append("<summary class=\"chat-collapsible-summary\">result  ").Append(HtmlEscape(resultSummary)).Append("</summary>");
            if (!string.IsNullOrEmpty(resultJson))
            {
                builder.Append("<pre class=\"chat-collapsible-body\">").Append(HtmlEscape(resultJson)).Append("</pre>");
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
                result is not null ? PrettyJson(result.Result) : null);
        }
        else
        {
            var innerBuilder = new StringBuilder();
            var lastCallName = string.Empty;
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
                    result is not null ? PrettyJson(result.Result) : null));
                lastCallName = call.Name ?? string.Empty;
            }

            return RenderToolGroupWrapper(contentId, calls.Count, lastCallName + "(…)", innerBuilder.ToString());
        }
    }

    /// <summary>Builds the full <c>div.chat-message</c> element for a message and its visible contents.</summary>
    public static string RenderMessage(
        string messageId,
        string roleLabel,
        IReadOnlyList<(string ElementId, string Html)> contents,
        DateTimeOffset? timestamp = null)
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

        builder.Append("</div></div>");
        return builder.ToString();
    }

    /// <summary>Builds the empty running-item container that hosts the running turn's messages.</summary>
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
        => string.Equals(roleLabel, "user", StringComparison.OrdinalIgnoreCase)
            ? "chat-user-message"
            : "chat-assistant-message";

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
                return RenderCollapsible(contentId, "chat-tool", label, PrettyJson(call.Arguments), SerializeContentJson(call));
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

                return RenderCollapsible(contentId, "chat-tool", $"tool result: {result.CallId}", PrettyJson(result.Result), SerializeContentJson(result));
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

    private static string RenderCollapsible(string contentId, string cssClass, string header, string body, string detailsJson)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content ").Append(cssClass).Append("\" data-copy-target data-details-target=\"").Append(HtmlEscape(detailsJson)).Append("\" data-inspect-target data-sticky-base-level=\"1\" id=\"").Append(contentId).Append("\">");
        builder.Append("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"0\">").Append(HtmlEscape(header)).Append("</summary>");
        if (!string.IsNullOrEmpty(body))
        {
            builder.Append("<pre class=\"chat-collapsible-body\">").Append(HtmlEscape(body)).Append("</pre>");
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
                catch (NotSupportedException)
                {
                    return value.ToString() ?? string.Empty;
                }
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

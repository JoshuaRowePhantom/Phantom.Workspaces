using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// Structural invariants for the chat HTML output pipeline (issue #893): a representative
/// end-to-end scenario is driven through <see cref="ChatOutputHtmlModel"/> and every recorded sink
/// operation is checked against the pipeline-wide invariants — no removed shell ids, no mutation
/// of persistent containers, approved id namespaces only, nested diff-target ids preserved by
/// grouping, and no duplicate element ids in the composed DOM content.
/// </summary>
public sealed class ChatOutputHtmlStructuralInvariantsTests
{
    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<Operation> Operations { get; } = [];

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));

        public void BeginBatch() { }

        public void EndBatch() { }
    }

    private static AgentChatHistoryItem TextMessage(ChatRole role, string text)
        => new() { Role = role, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem ToolCallMessage(string toolName, string callId)
        => new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent(callId, toolName)] };

    private static AgentChatHistoryItem ToolResultMessage(string callId)
        => new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent(callId, "result")] };

    /// <summary>
    /// Drives a representative multi-chunk scenario through the model: 205 history items with a
    /// tool run, live promotion/extension, a removal, a streaming running item, and a completion
    /// transition. Returns every recorded operation.
    /// </summary>
    private static async Task<(RecordingSink Sink, ChatOutputHtmlModel Model)> RunRepresentativeScenarioAsync()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 200).Select(i => TextMessage(ChatRole.User, $"text {i}")));
        history.Add(ToolCallMessage("tool_a", "c1"));
        history.Add(ToolResultMessage("c1"));
        history.Add(ToolCallMessage("tool_b", "c2"));
        history.Add(ToolResultMessage("c2"));
        history.Add(TextMessage(ChatRole.Assistant, "history reply"));

        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        var model = new ChatOutputHtmlModel(history, running, () => true, sink);
        await model.HistoryLoaded;

        // Live tool-call promotion and extension.
        history.Add(ToolCallMessage("tool_c", "c3"));
        history.Add(ToolCallMessage("tool_d", "c4"));
        history.Add(ToolResultMessage("c3"));
        history.Add(ToolCallMessage("tool_e", "c5"));

        // Live text after a group, then removal of a grouped member.
        history.Add(TextMessage(ChatRole.Assistant, "after group"));
        history.RemoveAt(206); // remove tool_d from the live group

        // Streaming running item with its own tool call/result.
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streaming"));
        runningItem.Items.Add(ToolCallMessage("running_tool", "run-c1"));
        runningItem.Items.Add(ToolResultMessage("run-c1"));
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "more streaming"));

        // Completion transition: running item is removed and its content lands in history.
        running.RemoveAt(0);
        history.Add(TextMessage(ChatRole.Assistant, "completed turn"));

        return (sink, model);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Invariants_NoOperationTargetsRemovedShellIds()
    {
        var (sink, model) = await RunRepresentativeScenarioAsync();
        using var _ = model;

        string[] removedIds = ["load-after", "history-before", "running-items-inside", "subagent-items-inside"];
        foreach (var op in sink.Operations)
        {
            Assert.DoesNotContain(op.Path, removedIds);
            foreach (var removedId in removedIds)
            {
                Assert.DoesNotContain($"id=\"{removedId}\"", op.Content);
            }
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Invariants_NoOperationReplacesPersistentContainers()
    {
        var (sink, model) = await RunRepresentativeScenarioAsync();
        using var _ = model;

        string[] persistentIds =
        [
            ChatOutputHtmlRenderer.HistoryContainerId,
            ChatOutputHtmlRenderer.RunningContainerId,
            ChatOutputHtmlRenderer.SubAgentPanelSentinelId,
        ];

        foreach (var op in sink.Operations)
        {
            if (persistentIds.Contains(op.Path))
            {
                Assert.NotEqual("remove", op.Kind);
                Assert.NotEqual(ChatOutputUpdateLocation.Replace, op.Location);
            }
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Invariants_EveryEmittedIdUsesApprovedNamespace()
    {
        var (sink, model) = await RunRepresentativeScenarioAsync();
        using var _ = model;

        // Approved namespaces: history-*, tool-group-*, run-* elements, plus the persistent
        // shell regions.
        var approvedPattern = new Regex(
            "^(history-\\d+|tool-group-\\d+|run-\\d+)(-.*)?$|" +
            $"^({ChatOutputHtmlRenderer.HistoryContainerId}|{ChatOutputHtmlRenderer.RunningContainerId}|{ChatOutputHtmlRenderer.SubAgentPanelSentinelId}|{ChatOutputHtmlRenderer.SubAgentPanelInnerId})$");

        foreach (var op in sink.Operations)
        {
            if (op.Kind is not ("update" or "remove"))
            {
                continue;
            }

            Assert.Matches(approvedPattern, op.Path);

            foreach (Match match in Regex.Matches(op.Content, "id=\"([^\"]+)\""))
            {
                Assert.Matches(approvedPattern, match.Groups[1].Value);
            }
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Invariants_GroupedMessagesRetainDiffTargetIds()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        // The grouped chunk blob must retain each member's message and contents-container ids so
        // per-content EmitDiff operations continue to resolve after grouping.
        var blob = sink.Operations.Single(op => op.Kind == "update").Content;
        for (var i = 0; i < 2; i++)
        {
            var messageId = ChatOutputHtmlRenderer.MessageId(i);
            Assert.Contains($"id=\"{messageId}\"", blob);
            Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ContentsContainerId(messageId)}\"", blob);
        }

        // A per-content update after grouping targets the nested content id directly.
        history[1] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("c2", "tool_b_renamed")],
        };

        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path.StartsWith(ChatOutputHtmlRenderer.MessageId(1), System.StringComparison.Ordinal) &&
            op.Content.Contains("tool_b_renamed"));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutput_GroupedToolCallHistoryItem_RendersCopyAndInspectTargetsOnToolBlocks()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolResultMessage("c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var blob = string.Concat(sink.Operations.Where(op => op.Kind == "update").Select(op => op.Content));

        var callBlock = ExtractOpeningTag(blob, "chat-tool-call");
        var resultBlock = ExtractOpeningTag(blob, "chat-tool-result");
        Assert.Contains("data-copy-target", callBlock);
        Assert.Contains("data-inspect-target", callBlock);
        Assert.Contains("data-copy-target", resultBlock);
        Assert.Contains("data-inspect-target", resultBlock);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutput_GroupedToolBlocks_MatchParityWithGenericMessageBlockGutters()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "generic block"),
            ToolCallMessage("tool_a", "c1"),
            ToolResultMessage("c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var blob = string.Concat(sink.Operations.Where(op => op.Kind == "update").Select(op => op.Content));

        // The generic text block carries copy + inspect markers; the grouped tool-call/result blocks
        // must carry the same pair (parity).
        var genericBlock = ExtractOpeningTag(blob, "chat-text");
        var callBlock = ExtractOpeningTag(blob, "chat-tool-call");
        var resultBlock = ExtractOpeningTag(blob, "chat-tool-result");

        foreach (var marker in new[] { "data-copy-target", "data-inspect-target" })
        {
            Assert.Contains(marker, genericBlock);
            Assert.Contains(marker, callBlock);
            Assert.Contains(marker, resultBlock);
        }
    }

    private static string ExtractOpeningTag(string html, string cssClass)
    {
        var classIndex = html.IndexOf(cssClass, System.StringComparison.Ordinal);
        Assert.True(classIndex >= 0, $"Expected an element with class '{cssClass}' in output.");
        var start = html.LastIndexOf('<', classIndex);
        var end = html.IndexOf('>', classIndex);
        Assert.True(start >= 0 && end >= 0);
        return html.Substring(start, end - start + 1);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Invariants_NoDuplicateIdsInRepresentativeDom()
    {
        var (sink, model) = await RunRepresentativeScenarioAsync();
        using var _ = model;

        // Replay the recorded operations against a miniature DOM seeded with the persistent shell
        // regions, then assert that every element id in the final DOM is unique and that every
        // operation resolved its target (no silently dropped commands).
        var root = MiniDom.CreateShellRoot();

        foreach (var op in sink.Operations)
        {
            switch (op.Kind)
            {
                case "remove":
                    root.RemoveById(op.Path);
                    break;

                case "update":
                    var target = root.FindById(op.Path);
                    Assert.True(target is not null, $"Operation {op.Location} targeted missing element '{op.Path}'.");
                    target!.Apply(op.Location, MiniDom.ParseFragment(op.Content));
                    break;
            }
        }

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var id in root.AllIds())
        {
            Assert.True(seen.Add(id), $"Duplicate id '{id}' in the final DOM.");
        }
    }

    /// <summary>
    /// Minimal DOM model for renderer-generated HTML: parses well-formed element markup (the
    /// renderer's output), supports the shell's applyCommand semantics, and enumerates ids.
    /// </summary>
    private sealed class MiniDom
    {
        private static readonly HashSet<string> VoidTags = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
        };

        private readonly List<MiniDom> children = [];
        private MiniDom? parent;

        public string? Id { get; private init; }

        public static MiniDom CreateShellRoot()
        {
            var root = new MiniDom();
            root.AddChild(new MiniDom { Id = ChatOutputHtmlRenderer.HistoryContainerId });
            root.AddChild(new MiniDom { Id = ChatOutputHtmlRenderer.RunningContainerId });
            var sentinel = new MiniDom { Id = ChatOutputHtmlRenderer.SubAgentPanelSentinelId };
            sentinel.AddChild(new MiniDom { Id = ChatOutputHtmlRenderer.SubAgentPanelInnerId });
            root.AddChild(sentinel);
            return root;
        }

        public static List<MiniDom> ParseFragment(string html)
        {
            var result = new List<MiniDom>();
            var stack = new Stack<MiniDom>();
            foreach (Match tag in Regex.Matches(html, "<(/?)([a-zA-Z][a-zA-Z0-9-]*)((?:[^>\"]|\"[^\"]*\")*?)(/?)>"))
            {
                var isClosing = tag.Groups[1].Value == "/";
                var tagName = tag.Groups[2].Value;
                var attributes = tag.Groups[3].Value;
                var isSelfClosing = tag.Groups[4].Value == "/" || VoidTags.Contains(tagName);

                if (isClosing)
                {
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }

                    continue;
                }

                var idMatch = Regex.Match(attributes, "\\bid=\"([^\"]*)\"");
                var element = new MiniDom { Id = idMatch.Success ? idMatch.Groups[1].Value : null };
                if (stack.Count > 0)
                {
                    stack.Peek().AddChild(element);
                }
                else
                {
                    result.Add(element);
                }

                if (!isSelfClosing)
                {
                    stack.Push(element);
                }
            }

            return result;
        }

        public void Apply(ChatOutputUpdateLocation location, List<MiniDom> fragment)
        {
            switch (location)
            {
                case ChatOutputUpdateLocation.Append:
                    foreach (var node in fragment)
                    {
                        this.AddChild(node);
                    }

                    break;

                case ChatOutputUpdateLocation.Prepend:
                    for (var i = 0; i < fragment.Count; i++)
                    {
                        fragment[i].parent = this;
                        this.children.Insert(i, fragment[i]);
                    }

                    break;

                case ChatOutputUpdateLocation.Replace:
                    this.SpliceIntoParent(fragment, offset: 0, removeSelf: true);
                    break;

                case ChatOutputUpdateLocation.After:
                    this.SpliceIntoParent(fragment, offset: 1, removeSelf: false);
                    break;

                case ChatOutputUpdateLocation.Before:
                    this.SpliceIntoParent(fragment, offset: 0, removeSelf: false);
                    break;
            }
        }

        public MiniDom? FindById(string id)
        {
            if (this.Id == id)
            {
                return this;
            }

            foreach (var child in this.children)
            {
                if (child.FindById(id) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        public void RemoveById(string id)
        {
            var node = this.FindById(id);
            node?.parent?.children.Remove(node);
        }

        public IEnumerable<string> AllIds()
        {
            if (this.Id is not null)
            {
                yield return this.Id;
            }

            foreach (var child in this.children)
            {
                foreach (var id in child.AllIds())
                {
                    yield return id;
                }
            }
        }

        private void AddChild(MiniDom child)
        {
            child.parent = this;
            this.children.Add(child);
        }

        private void SpliceIntoParent(List<MiniDom> fragment, int offset, bool removeSelf)
        {
            var container = this.parent!;
            var index = container.children.IndexOf(this);
            if (removeSelf)
            {
                container.children.RemoveAt(index);
            }
            else
            {
                index += offset;
            }

            for (var i = 0; i < fragment.Count; i++)
            {
                fragment[i].parent = container;
                container.children.Insert(index + i, fragment[i]);
            }
        }
    }
}

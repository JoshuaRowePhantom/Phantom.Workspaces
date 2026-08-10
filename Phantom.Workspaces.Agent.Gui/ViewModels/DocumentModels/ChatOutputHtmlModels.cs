using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Renders a single chat message as incremental HTML operations against an
/// <see cref="IChatOutputHtmlSink"/>. The full element is produced once via <see cref="BuildHtml"/>
/// (for insertion); subsequent <see cref="Update"/>/<see cref="Refresh"/> calls emit only the
/// per-content operations needed to reconcile the DOM, reusing stable element ids so unchanged nodes
/// are preserved. The element id is injected at construction and is immutable: history callers pass
/// <see cref="ChatOutputHtmlRenderer.MessageId"/>, running-item callers pass
/// <see cref="ChatOutputHtmlRenderer.RunningMessageId"/>.
/// </summary>
internal sealed class ChatMessageHtmlModel
{
    private sealed record ContentBinding(string Key, string ElementId, string Html, bool IsUsageInspect = false);

    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private readonly List<ContentBinding> bindings = [];
    private AgentChatHistoryItem source;
    private string? renderedRoleLabel;
    private bool hasRendered;
    private bool lastReasoningVisible;
    private bool isInsideMessageLevelToolGroup;
    private Dictionary<string, FunctionResultContent>? supplementalResults;

    public ChatMessageHtmlModel(
        int sourceIndex,
        string elementId,
        AgentChatHistoryItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.SourceIndex = sourceIndex;
        this.ElementId = elementId;
        this.source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.sink = sink;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;
        this.Render(emit: false);
    }

    /// <summary>The index of this message in its source collection at creation/render-plan time.</summary>
    public int SourceIndex { get; }

    /// <summary>The immutable DOM element id of this message's outer element.</summary>
    public string ElementId { get; }

    /// <summary>The source item this model currently renders.</summary>
    public AgentChatHistoryItem Source => this.source;

    /// <summary>Set once the message element has been inserted into the DOM by its transformer.</summary>
    public bool IsInserted { get; set; }

    /// <summary>
    /// True when this message is a member of a message-level <c>chat-tool-group</c> — i.e. its
    /// element lives inside a <c>chat-tool-group-body</c>. While set, any run of
    /// <see cref="FunctionCallContent"/> in this message renders as flat
    /// <c>chat-tool-group-item</c> sibling bindings rather than being wrapped in an inner
    /// <c>chat-tool-group-wrapper</c>. This preserves the structural invariant that a
    /// <c>tools (…)</c> group is never a descendant of another <c>tools (…)</c> group even when
    /// streaming grows a grouped member's tool-call count past 1 (issue #1123).
    /// </summary>
    public bool IsInsideMessageLevelToolGroup => this.isInsideMessageLevelToolGroup;

    /// <summary>
    /// Sets <see cref="IsInsideMessageLevelToolGroup"/>. When the value changes the content
    /// bindings are recomputed so subsequent <see cref="BuildHtml"/> reflects the new mode.
    /// Emits DOM operations only when <paramref name="emit"/> is true (used when the flag flips
    /// while the message is live and no enclosing Replace will subsume the change).
    /// </summary>
    internal void SetIsInsideMessageLevelToolGroup(bool value, bool emit)
    {
        if (this.isInsideMessageLevelToolGroup == value)
        {
            return;
        }

        this.isInsideMessageLevelToolGroup = value;
        this.Render(emit);
    }

    /// <summary>
    /// True when the message has been rendered and produced no visible content bindings (an empty
    /// message, a whitespace-only message, or a message whose contents were all filtered out — e.g.
    /// reasoning while reasoning is hidden). Such messages render no visible DOM element and must be
    /// transparent to tool-call grouping.
    /// </summary>
    public bool ProducesNoVisibleContent => this.hasRendered && this.bindings.Count == 0;

    /// <summary>
    /// Returns true if this message's source contains a <see cref="FunctionCallContent"/> with the
    /// given <paramref name="callId"/>. Used by the transformer to locate the matching call slot when
    /// a tool-result message arrives in a separate message.
    /// </summary>
    public bool HasCallWithId(string? callId)
    {
        if (callId is null)
        {
            return false;
        }

        foreach (var content in this.source.Contents)
        {
            if (content is FunctionCallContent call && call.CallId == callId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Injects a <see cref="FunctionResultContent"/> from a separate tool-role message into this
    /// message's rendering so the result appears nested under its matching call item. Recomputes the
    /// content bindings immediately (so a later <see cref="BuildHtml"/> includes the result) and
    /// emits reconciliation operations only when the message is already in the DOM.
    /// </summary>
    public void AddSupplementalResult(FunctionResultContent result)
    {
        if (result.CallId is null)
        {
            return;
        }

        this.supplementalResults ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        this.supplementalResults[result.CallId] = result;

        this.Render(emit: this.IsInserted);
    }

    /// <summary>Builds the full message element for initial insertion from the current bindings.</summary>
    public string BuildHtml()
    {
        var roleLabel = this.source.Role.Value;
        this.renderedRoleLabel = roleLabel;
        string? jumpLinkHtml = null;
        var parentToolCallId = this.source.Contents
            .Select(CopilotSdkStreamAdapter.GetParentToolCallId)
            .FirstOrDefault(id => id is not null);
        if (parentToolCallId is not null && this.resolveSubAgentId is not null)
        {
            var subAgentId = this.resolveSubAgentId(parentToolCallId);
            if (subAgentId is not null)
            {
                jumpLinkHtml = ChatOutputHtmlRenderer.RenderSubAgentJumpLink(subAgentId);
            }
        }

        return ChatOutputHtmlRenderer.RenderMessage(
            this.ElementId,
            roleLabel,
            this.bindings.Select(binding => (binding.ElementId, binding.Html)).ToList(),
            this.source.Timestamp,
            jumpLinkHtml);
    }

    /// <summary>
    /// Returns only the concatenated binding HTML without the outer <c>&lt;div class="chat-message"&gt;</c>
    /// frame or role header. Used when this message is a grouped member inside a
    /// <see cref="ToolCallGroupHtmlModel"/> — the group itself owns the single message frame and header
    /// (issue #1225).
    /// </summary>
    public string BuildGroupedMemberHtml()
        => string.Concat(this.bindings.Where(b => !b.IsUsageInspect).Select(b => b.Html));

    /// <summary>
    /// Returns usage-inspect bindings that must remain outside closed message-level tool groups so
    /// their gutter affordances are visible even when tool details stay collapsed.
    /// </summary>
    public string BuildGroupedMemberPostGroupHtml()
        => string.Concat(this.bindings.Where(b => b.IsUsageInspect).Select(b => b.Html));

    public void Update(AgentChatHistoryItem newSource)
    {
        if (ReferenceEquals(newSource, this.source))
        {
            return;
        }

        this.source = newSource;
        this.Render(emit: true);
    }

    public void Refresh() => this.Render(emit: true);

    internal void Render(bool emit)
    {
        var includeReasoning = this.isReasoningVisible();
        var reasoningChanged = !this.hasRendered || includeReasoning != this.lastReasoningVisible;
        var visibilityChanged = reasoningChanged;

        var roleLabel = this.source.Role.Value;
        var isDiagnostic = string.Equals(
            roleLabel,
            AgentChatHistoryItem.DiagnosticChatRole.Value,
            StringComparison.OrdinalIgnoreCase);
        var isHelp = string.Equals(
            roleLabel,
            AgentChatHistoryItem.HelpChatRole.Value,
            StringComparison.OrdinalIgnoreCase);

        // Pre-scan: build a CallId → result lookup for content-level call+result pairing.
        Dictionary<string, FunctionResultContent>? resultLookup = null;
        foreach (var content in this.source.Contents)
        {
            if (content is FunctionResultContent result && result.CallId is not null)
            {
                resultLookup ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
                resultLookup.TryAdd(result.CallId, result);
            }
        }

        // Merge supplemental results injected from a separate tool-role message (cross-message pairing).
        if (this.supplementalResults is not null)
        {
            resultLookup ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
            foreach (var (callId, supplemental) in this.supplementalResults)
            {
                resultLookup.TryAdd(callId, supplemental);
            }
        }

        // Determine which result CallIds are "claimed" (have a matching call in this message).
        HashSet<string>? claimedResultCallIds = null;
        if (resultLookup is not null)
        {
            foreach (var content in this.source.Contents)
            {
                if (content is FunctionCallContent call && call.CallId is not null && resultLookup.ContainsKey(call.CallId))
                {
                    claimedResultCallIds ??= new HashSet<string>(StringComparer.Ordinal);
                    claimedResultCallIds.Add(call.CallId);
                }
            }
        }

        var newBindings = new List<ContentBinding>(this.source.Contents.Count);
        var contentIndex = 0;
        var contentCount = this.source.Contents.Count;

        while (contentIndex < contentCount)
        {
            var content = this.source.Contents[contentIndex];

            if (content is FunctionCallContent firstCall)
            {
                // Grouped-member mode: never emit an inner content-level `chat-tool-group-wrapper`
                // inside the enclosing message-level group body. Each FunctionCallContent becomes
                // its own flat `chat-tool-group-item` binding (issue #1123).
                if (this.isInsideMessageLevelToolGroup)
                {
                    var flatContentId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);
                    var flatKeyParts = new List<string>(2)
                    {
                        ChatOutputHtmlRenderer.ComputeContentKey(firstCall, isDiagnostic),
                    };
                    if (firstCall.CallId is not null &&
                        resultLookup is not null &&
                        resultLookup.TryGetValue(firstCall.CallId, out var flatMatchedResult))
                    {
                        flatKeyParts.Add(ChatOutputHtmlRenderer.ComputeContentKey(flatMatchedResult, isDiagnostic));
                    }

                    var flatKey = "flat:" + string.Join("\x02", flatKeyParts);
                    var flatHtml = ChatOutputHtmlRenderer.RenderToolGroup(flatContentId, new[] { firstCall }, resultLookup);
                    newBindings.Add(new ContentBinding(flatKey, flatContentId, flatHtml));
                    contentIndex++;
                    continue;
                }

                // Collect the maximal run of consecutive FunctionCallContent items.
                var calls = new List<FunctionCallContent> { firstCall };
                contentIndex++;
                while (contentIndex < contentCount && this.source.Contents[contentIndex] is FunctionCallContent nextCall)
                {
                    calls.Add(nextCall);
                    contentIndex++;
                }

                var elementId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);

                // Composite key: concatenation of each call's key and its matched result's key.
                var keyParts = new List<string>(calls.Count * 2);
                foreach (var call in calls)
                {
                    keyParts.Add(ChatOutputHtmlRenderer.ComputeContentKey(call, isDiagnostic));
                    if (call.CallId is not null && resultLookup is not null && resultLookup.TryGetValue(call.CallId, out var matchedResult))
                    {
                        keyParts.Add(ChatOutputHtmlRenderer.ComputeContentKey(matchedResult, isDiagnostic));
                    }
                }

                var groupKey = "group:" + string.Join("\x02", keyParts);
                var groupHtml = ChatOutputHtmlRenderer.RenderToolGroup(elementId, calls, resultLookup);
                newBindings.Add(new ContentBinding(groupKey, elementId, groupHtml));
                continue;
            }

            // Skip FunctionResultContent items claimed by a tool group.
            if (content is FunctionResultContent claimedResult &&
                claimedResult.CallId is not null &&
                claimedResultCallIds is not null &&
                claimedResultCallIds.Contains(claimedResult.CallId))
            {
                contentIndex++;
                continue;
            }

            var contentId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);
            var html = ChatOutputHtmlRenderer.RenderContent(contentId, content, includeReasoning, isDiagnostic, isHelp, this.toolFactory, this.statusSink);
            if (html is not null)
            {
                var key = ChatOutputHtmlRenderer.ComputeContentKey(content, isDiagnostic);
                newBindings.Add(new ContentBinding(key, contentId, html, content is UsageContent));
            }

            contentIndex++;
        }

        if (emit)
        {
            this.EmitDiff(newBindings, roleLabel, visibilityChanged);
        }

        this.bindings.Clear();
        this.bindings.AddRange(newBindings);
        this.hasRendered = true;
        this.lastReasoningVisible = includeReasoning;
        this.renderedRoleLabel = roleLabel;
    }

    private void EmitDiff(List<ContentBinding> newBindings, string roleLabel, bool visibilityChanged)
    {
        if (!string.Equals(this.renderedRoleLabel, roleLabel, StringComparison.Ordinal))
        {
            this.sink.UpdateContent(
                ChatOutputHtmlRenderer.HeaderId(this.ElementId),
                ChatOutputUpdateLocation.Replace,
                ChatOutputHtmlRenderer.RenderHeader(this.ElementId, roleLabel));
        }

        for (var index = 0; index < newBindings.Count; index++)
        {
            if (index < this.bindings.Count)
            {
                if (!visibilityChanged && this.bindings[index].Key == newBindings[index].Key)
                {
                    continue;
                }

                this.sink.UpdateContent(newBindings[index].ElementId, ChatOutputUpdateLocation.Replace, newBindings[index].Html);
            }
            else
            {
                this.sink.UpdateContent(
                    ChatOutputHtmlRenderer.ContentsContainerId(this.ElementId),
                    ChatOutputUpdateLocation.Append,
                    newBindings[index].Html);
            }
        }

        for (var index = newBindings.Count; index < this.bindings.Count; index++)
        {
            this.sink.RemoveContent(this.bindings[index].ElementId);
        }
    }
}

/// <summary>
/// Renders a run of consecutive tool-call history items as a single collapsible
/// <c>details</c> element. Children are <see cref="ChatMessageHtmlModel"/> instances whose HTML
/// is placed intact inside the expanded body (so later per-content diffs can still target their
/// child ids). The summary line lists the distinct tool names across all members (in first-seen
/// encounter order across the members' <see cref="FunctionCallContent"/> items) and the total
/// call count. The group id is derived from the first member's source index and never changes
/// for the life of the group.
/// </summary>
internal sealed class ToolCallGroupHtmlModel
{
    private readonly IChatOutputHtmlSink sink;
    private readonly List<ChatMessageHtmlModel> members = [];

    public ToolCallGroupHtmlModel(int firstHistoryIndex, string groupId, IChatOutputHtmlSink sink, ChatMessageHtmlModel firstMember)
    {
        this.FirstHistoryIndex = firstHistoryIndex;
        this.GroupId = groupId;
        this.sink = sink;
        this.members.Add(firstMember);
    }

    /// <summary>The source index of the first (top-level) member the group replaced.</summary>
    public int FirstHistoryIndex { get; }

    public string GroupId { get; }

    /// <summary>The distinct tool names in the group, in first-seen (encounter) order across all
    /// members' <see cref="FunctionCallContent"/> items.</summary>
    public IReadOnlyList<string> DistinctToolNames
    {
        get
        {
            var seen = new List<string>();
            foreach (var member in this.members)
            {
                foreach (var content in member.Source.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        var name = call.Name ?? string.Empty;
                        if (!seen.Contains(name))
                        {
                            seen.Add(name);
                        }
                    }
                }
            }

            return seen;
        }
    }

    /// <summary>Total number of <see cref="FunctionCallContent"/> items across all members.</summary>
    public int CallCount
    {
        get
        {
            var count = 0;
            foreach (var member in this.members)
            {
                foreach (var content in member.Source.Contents)
                {
                    if (content is FunctionCallContent)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Builds the complete group element for DOM insertion, with <paramref name="firstMessageHtml"/>
    /// (or the concatenated member HTML) pre-placed inside the body container.
    /// The group owns the single <c>&lt;div class="chat-message assistant"&gt;</c> frame and header
    /// so grouped members contribute only their binding HTML (issue #1225).
    /// </summary>
    public string BuildHtml(string firstMessageHtml, string? postGroupHtml = null)
        => ChatOutputHtmlRenderer.RenderToolCallGroup(
            this.GroupId,
            this.DistinctToolNames,
            this.CallCount,
            firstMessageHtml,
            this.members[0].Source.Timestamp,
            postGroupHtml ?? this.members[0].BuildGroupedMemberPostGroupHtml());

    /// <summary>Appends <paramref name="model"/> to the group body in the DOM and updates the summary badge.</summary>
    public void AppendItem(ChatMessageHtmlModel model)
    {
        this.members.Add(model);

        var groupedMemberHtml = model.BuildGroupedMemberHtml();
        if (!string.IsNullOrEmpty(groupedMemberHtml))
        {
            this.sink.UpdateContent(
                ChatOutputHtmlRenderer.ToolGroupBodyId(this.GroupId),
                ChatOutputUpdateLocation.Append,
                groupedMemberHtml);
        }

        var postGroupHtml = model.BuildGroupedMemberPostGroupHtml();
        if (!string.IsNullOrEmpty(postGroupHtml))
        {
            this.sink.UpdateContent(
                ChatOutputHtmlRenderer.ContentsContainerId(this.GroupId),
                ChatOutputUpdateLocation.Append,
                postGroupHtml);
        }
        model.IsInserted = true;

        this.EmitSummaryUpdate();
    }

    /// <summary>
    /// Updates group state (call count, tool names) without emitting any sink operations.
    /// Used by render-plan/chunk generation and run rebuilds, where the group HTML is produced
    /// as one blob rather than by incremental DOM operations.
    /// </summary>
    internal void AppendItemStateOnly(ChatMessageHtmlModel model)
    {
        this.members.Add(model);
    }

    /// <summary>
    /// Re-emits the group summary from the current member set. Called after a grouped member's
    /// tool-call composition changes during streaming so that the outer <c>tools (…)</c> label
    /// reflects the current set of tool names and total call count (issue #1123).
    /// </summary>
    public void RefreshSummary()
        => this.EmitSummaryUpdate();

    private void EmitSummaryUpdate()
    {
        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.ToolGroupSummaryId(this.GroupId),
            ChatOutputUpdateLocation.Replace,
            ChatOutputHtmlRenderer.RenderToolCallGroupSummary(this.GroupId, this.DistinctToolNames, this.CallCount));
    }
}

/// <summary>
/// Discriminated render target for <see cref="ChatMessageHtmlTransformer"/> and the history render
/// plan. Each source item maps to a model plus an optional group; the group is set when the item
/// belongs to a tool-call run. <see cref="HasDomElement"/> is false for slots that render no DOM
/// element of their own (suppressed diagnostics and result-only messages injected into a call).
/// </summary>
internal sealed class RenderSlot
{
    public RenderSlot(ChatMessageHtmlModel model) => this.Model = model;

    public ChatMessageHtmlModel Model { get; }

    /// <summary>Non-null when this item has been merged into a tool-call group.</summary>
    public ToolCallGroupHtmlModel? Group { get; set; }

    /// <summary>
    /// True when this slot owns (or will own) a DOM element — either top-level or nested inside a
    /// group body. False for suppressed diagnostics and result-only messages whose results were
    /// injected into matched call models.
    /// </summary>
    public bool HasDomElement { get; set; } = true;

    /// <summary>True only for the first rendered member that a group replaced at the top level.</summary>
    public bool IsTopLevelFirstGroupMember { get; set; }
}

/// <summary>
/// Transforms a chat-message source collection into <see cref="ChatMessageHtmlModel"/> instances,
/// emitting insertion/removal operations against a parent container in the DOM. Element ids are
/// produced by the injected <c>elementIdForSourceIndex</c>; tool-call group ids by
/// <c>groupIdForSourceIndex</c>. Call-id lookup uses the shared, model-level map so tool results
/// can match calls across chunks, phases, and running items.
/// </summary>
internal sealed class ChatMessageHtmlTransformer : CollectionTransformer<AgentChatHistoryItem, RenderSlot>
{
    private enum StructuralCategory
    {
        ToolCallOnly,
        ResultOnly,
        Normal,
    }

    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly string containerPath;
    private readonly Func<int, string> elementIdForSourceIndex;
    private readonly Func<int, string> groupIdForSourceIndex;
    private readonly Dictionary<string, RenderSlot> sharedSlotByCallId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private int nextCreateIndex;

    public ChatMessageHtmlTransformer(
        IReadOnlyList<AgentChatHistoryItem> source,
        List<RenderSlot> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        string containerPath,
        Func<int, string> elementIdForSourceIndex,
        Func<int, string> groupIdForSourceIndex,
        Dictionary<string, RenderSlot> sharedSlotByCallId,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null,
        int preloadedCount = 0,
        IReadOnlyList<NotifyCollectionChangedEventArgs>? bufferedEvents = null)
        : base(source, target)
    {
        ArgumentNullException.ThrowIfNull(elementIdForSourceIndex);
        ArgumentNullException.ThrowIfNull(groupIdForSourceIndex);
        ArgumentNullException.ThrowIfNull(sharedSlotByCallId);
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.containerPath = containerPath;
        this.elementIdForSourceIndex = elementIdForSourceIndex;
        this.groupIdForSourceIndex = groupIdForSourceIndex;
        this.sharedSlotByCallId = sharedSlotByCallId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;
        this.nextCreateIndex = preloadedCount;

        // Preloaded slots (from the Phase B render plan) are already in `target` and in the DOM.
        // When the buffered events captured during loading are supplied, replaying them applies
        // every mutation (tail adds, mid-list inserts, replaces, removes, moves) exactly once,
        // since the buffer is precisely the delta between the snapshot and the current source.
        // Without a buffer, only items appended after the snapshot are created and inserted.
        if (bufferedEvents is not null)
        {
            foreach (var bufferedEvent in bufferedEvents)
            {
                this.ApplySourceEvent(bufferedEvent);
            }
        }
        else
        {
            for (var index = this.Target.Count; index < source.Count; index++)
            {
                var slot = this.Create(source[index]);
                this.Target.Add(slot);
                this.OnInsert(index, slot);
            }
        }
    }

    protected override RenderSlot Create(AgentChatHistoryItem sourceItem)
    {
        var index = this.nextCreateIndex++;
        return new(new ChatMessageHtmlModel(
            index,
            this.elementIdForSourceIndex(index),
            sourceItem,
            this.isReasoningVisible,
            this.sink,
            this.toolFactory,
            this.statusSink,
            this.resolveSubAgentId));
    }

    protected override void Update(RenderSlot target, AgentChatHistoryItem sourceItem)
    {
        var oldSource = target.Model.Source;
        if (ReferenceEquals(oldSource, sourceItem))
        {
            return;
        }

        if (this.Categorize(oldSource) == this.Categorize(sourceItem))
        {
            target.Model.Update(sourceItem);

            if (ContainsFunctionCalls(oldSource) || ContainsFunctionCalls(sourceItem))
            {
                this.RemoveCallIdsFromIndex(target);
                this.AddCallIdsToIndex(sourceItem, target);

                // If this slot is a member of a message-level tool group and its tool-call
                // composition may have changed (added/removed/renamed calls), refresh the outer
                // group's summary so the `tools (…)` label and call-count badge stay accurate
                // (issue #1123). The member is already rendered in "grouped-member" flat mode
                // (flag set at join time), so the member's own DOM re-render above never emits
                // a nested content-level wrapper.
                if (target.Group is { } enclosingGroup)
                {
                    enclosingGroup.RefreshSummary();
                }
            }

            // A result-only message whose results are injected has no DOM element; keep the matched
            // call models up to date with the replacement results.
            if (!target.HasDomElement && IsToolResultOnlyItem(sourceItem))
            {
                this.TryInjectResults(sourceItem);
            }

            return;
        }

        // Structural category changed (e.g. tool-call ↔ text): rebuild locally as removal + insert.
        var index = this.Target.IndexOf(target);
        this.RebuildForReplace(index, target, sourceItem);
    }

    protected override void OnInsert(int index, RenderSlot slot)
    {
        this.AddCallIdsToIndex(slot.Model.Source, slot);
        this.ClassifyAndInsert(index, slot);
    }

    protected override void OnRemoveAt(int index, RenderSlot slot)
    {
        this.RemoveCallIdsFromIndex(slot);

        if (!slot.HasDomElement)
        {
            return;
        }

        if (slot.Group is { } group)
        {
            this.RebuildGroupAfterRemoval(group, slot);
            return;
        }

        this.sink.RemoveContent(slot.Model.ElementId);
    }

    protected override void OnMove(int oldIndex, int newIndex, RenderSlot slot)
    {
        // Moves cannot be expressed as a safe local DOM operation once grouping is involved;
        // rebuild this transformer's whole container region from the (already reordered) target.
        var removedTopLevelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in this.Target)
        {
            if (!existing.HasDomElement)
            {
                continue;
            }

            var topLevelId = existing.Group?.GroupId ?? existing.Model.ElementId;
            if (removedTopLevelIds.Add(topLevelId))
            {
                this.sink.RemoveContent(topLevelId);
            }
        }

        foreach (var existing in this.Target)
        {
            existing.Group = null;
            existing.IsTopLevelFirstGroupMember = false;
            existing.HasDomElement = false;
            existing.Model.IsInserted = false;
            existing.Model.SetIsInsideMessageLevelToolGroup(false, emit: false);
        }

        for (var index = 0; index < this.Target.Count; index++)
        {
            this.ClassifyAndInsert(index, this.Target[index]);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var slot in this.Target)
        {
            this.RemoveCallIdsFromIndex(slot);
        }
    }

    /// <summary>
    /// Classifies <paramref name="slot"/> (no-DOM, grouped, or standalone) and emits the DOM
    /// operations that realize it. This is the normative live-insert algorithm: the first
    /// renderable item appends into the configured container; later items insert after the previous
    /// renderable top-level element or its group.
    /// </summary>
    private void ClassifyAndInsert(int index, RenderSlot slot)
    {
        var sourceItem = slot.Model.Source;

        // A message containing only FunctionResultContent items produces no DOM element of its own
        // when every result matches a known call: each result is injected into the matched call
        // model so it renders nested under its call item. Any unmatched result keeps the whole
        // message standalone.
        if (IsToolResultOnlyItem(sourceItem) && this.TryInjectResults(sourceItem))
        {
            slot.HasDomElement = false;
            return;
        }

        // A message that renders no visible content (empty, whitespace-only, or fully filtered — e.g.
        // reasoning while reasoning is hidden) produces no DOM element of its own. Suppressing it here
        // avoids a stray empty bubble and keeps it transparent to tool-call grouping.
        if (slot.Model.ProducesNoVisibleContent)
        {
            slot.HasDomElement = false;
            return;
        }

        slot.HasDomElement = true;

        if (IsToolCallOnlyItem(sourceItem))
        {
            var groupablePredecessor = this.FindGroupablePredecessor(index);
            if (groupablePredecessor is not null)
            {
                if (groupablePredecessor.Group is { } existingGroup)
                {
                    // Silently switch the joining member into grouped-member render mode so its
                    // BuildHtml emits flat `chat-tool-group-item` bindings rather than an inner
                    // `chat-tool-group-wrapper` (issue #1123).
                    slot.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                    existingGroup.AppendItem(slot.Model);
                    slot.Group = existingGroup;
                    return;
                }

                // Previous item was a standalone tool call: promote both into a new group whose id
                // is derived from the previous item's global source index.
                groupablePredecessor.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                slot.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                var group = new ToolCallGroupHtmlModel(
                    groupablePredecessor.Model.SourceIndex,
                    this.groupIdForSourceIndex(groupablePredecessor.Model.SourceIndex),
                    this.sink,
                    groupablePredecessor.Model);
                this.sink.UpdateContent(
                    groupablePredecessor.Model.ElementId,
                    ChatOutputUpdateLocation.Replace,
                    group.BuildHtml(groupablePredecessor.Model.BuildGroupedMemberHtml()));
                groupablePredecessor.Group = group;
                groupablePredecessor.IsTopLevelFirstGroupMember = true;

                group.AppendItem(slot.Model);
                slot.Group = group;
                return;
            }
        }

        // Standalone insert (non-tool-call, or first/isolated tool call with no adjacent group).
        var previous = this.FindPreviousDomSlot(index);
        if (previous is not null)
        {
            this.sink.UpdateContent(
                previous.Group?.GroupId ?? previous.Model.ElementId,
                ChatOutputUpdateLocation.After,
                slot.Model.BuildHtml());
        }
        else
        {
            var next = this.FindNextDomSlot(index);
            if (next is not null)
            {
                this.sink.UpdateContent(
                    next.Group?.GroupId ?? next.Model.ElementId,
                    ChatOutputUpdateLocation.Before,
                    slot.Model.BuildHtml());
            }
            else
            {
                this.sink.UpdateContent(this.containerPath, ChatOutputUpdateLocation.Append, slot.Model.BuildHtml());
            }
        }

        slot.Model.IsInserted = true;
    }

    /// <summary>
    /// Rebuilds the tool-call group that <paramref name="excludedSlot"/> is leaving: the group's
    /// outer element is replaced by the rebuilt group (or the sole remaining member's standalone
    /// element), or removed entirely when no members remain. Never leaves an empty group or
    /// duplicate ids in the DOM.
    /// </summary>
    private void RebuildGroupAfterRemoval(ToolCallGroupHtmlModel group, RenderSlot excludedSlot)
    {
        var members = this.Target
            .Where(candidate => ReferenceEquals(candidate.Group, group) && !ReferenceEquals(candidate, excludedSlot))
            .ToList();

        if (members.Count == 0)
        {
            this.sink.RemoveContent(group.GroupId);
            return;
        }

        var first = members[0];
        if (members.Count == 1)
        {
            first.Group = null;
            first.IsTopLevelFirstGroupMember = false;
            // Sole surviving member goes back to standalone rendering — content-level wrapper is
            // allowed again since there is no enclosing message-level group body (issue #1123).
            first.Model.SetIsInsideMessageLevelToolGroup(false, emit: false);
            this.sink.UpdateContent(group.GroupId, ChatOutputUpdateLocation.Replace, first.Model.BuildHtml());
            return;
        }

        var rebuiltGroup = new ToolCallGroupHtmlModel(
            first.Model.SourceIndex,
            this.groupIdForSourceIndex(first.Model.SourceIndex),
            this.sink,
            first.Model);
        first.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
        var body = new StringBuilder(first.Model.BuildGroupedMemberHtml());
        var postGroup = new StringBuilder(first.Model.BuildGroupedMemberPostGroupHtml());
        first.Group = rebuiltGroup;
        first.IsTopLevelFirstGroupMember = true;

        for (var memberIndex = 1; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            member.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
            rebuiltGroup.AppendItemStateOnly(member.Model);
            member.Group = rebuiltGroup;
            member.IsTopLevelFirstGroupMember = false;
            body.Append(member.Model.BuildGroupedMemberHtml());
            postGroup.Append(member.Model.BuildGroupedMemberPostGroupHtml());
        }

        this.sink.UpdateContent(group.GroupId, ChatOutputUpdateLocation.Replace, rebuiltGroup.BuildHtml(body.ToString(), postGroup.ToString()));
    }

    private void RebuildForReplace(int index, RenderSlot slot, AgentChatHistoryItem newItem)
    {
        this.RemoveCallIdsFromIndex(slot);

        if (slot.HasDomElement)
        {
            if (slot.Group is { } group)
            {
                this.RebuildGroupAfterRemoval(group, slot);
            }
            else
            {
                this.sink.RemoveContent(slot.Model.ElementId);
            }
        }

        var fresh = new RenderSlot(new ChatMessageHtmlModel(
            slot.Model.SourceIndex,
            slot.Model.ElementId,
            newItem,
            this.isReasoningVisible,
            this.sink,
            this.toolFactory,
            this.statusSink,
            this.resolveSubAgentId));
        this.Target[index] = fresh;

        this.AddCallIdsToIndex(newItem, fresh);
        this.ClassifyAndInsert(index, fresh);
    }

    /// <summary>
    /// Deterministically recovers from a DOM command that failed because the element with id
    /// <paramref name="failedPath"/> (a message element or tool-group wrapper owned by this
    /// transformer) was missing. The repair range starts at the matching slot — extended backward
    /// to the first member of its containing group, so groups are always rebuilt whole — and runs
    /// to the end of history: every top-level element in the range is removed (the browser treats
    /// removal of a missing element as a no-op) and re-inserted via <see cref="ClassifyAndInsert"/>.
    /// The first re-inserted slot anchors on the last still-attached slot before the range, or
    /// falls back to <c>Append</c> into the persistent container when none exists. Repairing the
    /// whole tail also restores any payload dropped by the failed command itself, since that
    /// payload's slot always follows the missing anchor. Returns false when no slot matches.
    /// </summary>
    internal bool RepairFailedElement(string failedPath)
    {
        var repairStart = -1;
        for (var i = 0; i < this.Target.Count; i++)
        {
            var slot = this.Target[i];
            if (slot.HasDomElement &&
                (slot.Model.ElementId == failedPath || slot.Group?.GroupId == failedPath))
            {
                repairStart = i;
                break;
            }
        }

        if (repairStart < 0)
        {
            return false;
        }

        if (this.Target[repairStart].Group is { } containingGroup)
        {
            for (var i = repairStart - 1; i >= 0; i--)
            {
                if (ReferenceEquals(this.Target[i].Group, containingGroup))
                {
                    repairStart = i;
                }
            }
        }

        var removedTopLevelIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = repairStart; i < this.Target.Count; i++)
        {
            var slot = this.Target[i];
            if (!slot.HasDomElement)
            {
                continue;
            }

            var topLevelId = slot.Group?.GroupId ?? slot.Model.ElementId;
            if (removedTopLevelIds.Add(topLevelId))
            {
                this.sink.RemoveContent(topLevelId);
            }
        }

        for (var i = repairStart; i < this.Target.Count; i++)
        {
            var slot = this.Target[i];
            slot.Group = null;
            slot.IsTopLevelFirstGroupMember = false;
            slot.HasDomElement = false;
            slot.Model.IsInserted = false;
            slot.Model.SetIsInsideMessageLevelToolGroup(false, emit: false);
        }

        for (var i = repairStart; i < this.Target.Count; i++)
        {
            this.ClassifyAndInsert(i, this.Target[i]);
        }

        return true;
    }

    private StructuralCategory Categorize(AgentChatHistoryItem item)
    {
        if (IsToolCallOnlyItem(item))
        {
            return StructuralCategory.ToolCallOnly;
        }

        if (IsToolResultOnlyItem(item))
        {
            return StructuralCategory.ResultOnly;
        }

        return StructuralCategory.Normal;
    }

    /// <summary>
    /// Attempts to inject every <see cref="FunctionResultContent"/> in <paramref name="item"/> into
    /// the call models matched via the shared call-id map. All results must match for injection to
    /// happen; returns false (and injects nothing) when any result is unmatched.
    /// </summary>
    private bool TryInjectResults(AgentChatHistoryItem item)
    {
        List<(RenderSlot MatchedSlot, FunctionResultContent Result)>? matches = null;
        foreach (var content in item.Contents)
        {
            var result = (FunctionResultContent)content;
            if (result.CallId is null || !this.sharedSlotByCallId.TryGetValue(result.CallId, out var matchedSlot))
            {
                return false;
            }

            (matches ??= []).Add((matchedSlot, result));
        }

        if (matches is null)
        {
            return false;
        }

        foreach (var (matchedSlot, result) in matches)
        {
            matchedSlot.Model.AddSupplementalResult(result);
        }

        return true;
    }

    private void AddCallIdsToIndex(AgentChatHistoryItem sourceItem, RenderSlot slot)
    {
        foreach (var content in sourceItem.Contents)
        {
            if (content is FunctionCallContent call && call.CallId is not null)
            {
                this.sharedSlotByCallId[call.CallId] = slot;
            }
        }
    }

    private void RemoveCallIdsFromIndex(RenderSlot slot)
    {
        var keysToRemove = this.sharedSlotByCallId
            .Where(kvp => ReferenceEquals(kvp.Value, slot))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            this.sharedSlotByCallId.Remove(key);
        }
    }

    private static bool ContainsFunctionCalls(AgentChatHistoryItem item)
    {
        foreach (var content in item.Contents)
        {
            if (content is FunctionCallContent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the item contains only <see cref="FunctionResultContent"/> items.
    /// Such messages are handled by cross-message result injection rather than message-level grouping.
    /// </summary>
    internal static bool IsToolResultOnlyItem(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in item.Contents)
        {
            if (content is not FunctionResultContent)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsToolCallOnlyItem(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in item.Contents)
        {
            if (content is not FunctionCallContent)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Looks up the slot whose source message contains a <see cref="FunctionCallContent"/> with the
    /// given <paramref name="callId"/> via the shared call-id map. Returns <see langword="null"/>
    /// if not found.
    /// </summary>
    internal RenderSlot? FindSlotWithCallId(string? callId)
    {
        if (callId is null)
        {
            return null;
        }

        this.sharedSlotByCallId.TryGetValue(callId, out var slot);
        return slot;
    }

    /// <summary>
    /// True when <paramref name="slot"/> renders no visible top-level DOM element and must be
    /// transparent to tool-call grouping: a result-only message injected into its matched call, a
    /// suppressed diagnostic or other slot with no DOM element, or a message that produced no
    /// visible content (empty / whitespace / fully-filtered).
    /// </summary>
    internal static bool IsGroupingTransparent(RenderSlot slot)
        => !slot.HasDomElement
            || IsToolResultOnlyItem(slot.Model.Source)
            || slot.Model.ProducesNoVisibleContent;

    /// <summary>
    /// Searches backwards from <paramref name="index"/> - 1, skipping every grouping-transparent
    /// slot (result-only messages, suppressed diagnostics, and messages that render no visible
    /// content), and returns the slot of the most recent source item that is either a tool-call-only
    /// message or already belongs to a group. Returns <see langword="null"/> when the search reaches
    /// the start of the collection or hits any displayed non-tool message: grouping never crosses
    /// visible text or other visible non-tool content.
    /// </summary>
    internal RenderSlot? FindGroupablePredecessor(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var candidate = this.Target[i];
            if (IsGroupingTransparent(candidate))
            {
                continue;
            }

            return candidate.HasDomElement &&
                   (candidate.Group is not null || IsToolCallOnlyItem(candidate.Model.Source))
                ? candidate
                : null;
        }

        return null;
    }

    private RenderSlot? FindPreviousDomSlot(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (this.Target[i].HasDomElement)
            {
                return this.Target[i];
            }
        }

        return null;
    }

    private RenderSlot? FindNextDomSlot(int index)
    {
        for (var i = index + 1; i < this.Target.Count; i++)
        {
            if (this.Target[i].HasDomElement)
            {
                return this.Target[i];
            }
        }

        return null;
    }

    internal static string GetLastToolName(AgentChatHistoryItem item)
    {
        for (var i = item.Contents.Count - 1; i >= 0; i--)
        {
            if (item.Contents[i] is FunctionCallContent call)
            {
                return call.Name ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

/// <summary>Renders a single running (in-progress) turn: an empty container that hosts its streaming messages.</summary>
internal sealed class RunningChatItemHtmlModel : IDisposable
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Dictionary<string, RenderSlot> sharedSlotByCallId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly List<RenderSlot> messageSlots = [];
    private ChatMessageHtmlTransformer? transformer;

    public RunningChatItemHtmlModel(
        string elementId,
        AgentChatRunningItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        Dictionary<string, RenderSlot> sharedSlotByCallId,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sharedSlotByCallId);
        this.ElementId = elementId;
        this.Source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.sink = sink;
        this.sharedSlotByCallId = sharedSlotByCallId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
    }

    public string ElementId { get; }

    public bool IsInserted { get; set; }

    public AgentChatRunningItem? Source { get; private set; }

    /// <summary>The empty container element; messages are appended into it once it is activated.</summary>
    public string BuildHtml() => ChatOutputHtmlRenderer.RenderRunningItemContainer(this.ElementId);

    /// <summary>
    /// Builds the inner message transformer, which appends the current and future messages into the
    /// (now inserted) container. Must be called after the container element has been inserted.
    /// </summary>
    public void Activate()
    {
        if (this.Source is null)
        {
            return;
        }

        this.transformer = new ChatMessageHtmlTransformer(
            this.Source.Items,
            this.messageSlots,
            this.sink,
            this.isReasoningVisible,
            containerPath: ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId),
            elementIdForSourceIndex: localIndex => ChatOutputHtmlRenderer.RunningMessageId(this.ElementId, localIndex),
            groupIdForSourceIndex: localIndex => $"{this.ElementId}-group-{localIndex}",
            sharedSlotByCallId: this.sharedSlotByCallId,
            toolFactory: this.toolFactory,
            statusSink: this.statusSink,
            preloadedCount: 0);
    }

    public void Update(AgentChatRunningItem? source)
    {
        var previousItems = this.Source?.Items;
        this.Source = source;

        if (this.transformer is null)
        {
            // Recover from a deferred activation: if the item was inserted while its source was
            // null (so Activate() no-opped), build the transformer now that a valid source has
            // arrived. Otherwise streaming updates would be silently dropped forever.
            if (this.IsInserted && source is not null)
            {
                this.Activate();
            }

            return;
        }

        if (ReferenceEquals(previousItems, source?.Items))
        {
            return;
        }

        this.transformer.Dispose();
        this.messageSlots.Clear();
        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId),
            ChatOutputUpdateLocation.Replace,
            $"<div class=\"chat-running-contents\" id=\"{ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId)}\"></div>");
        this.Activate();
    }

    public void Refresh()
    {
        foreach (var slot in this.messageSlots)
        {
            slot.Model.Refresh();
        }
    }

    /// <summary>
    /// Re-establishes the insertion point after a DOM failure by discarding the current transformer,
    /// clearing message slots, and re-inserting the container via <c>Append</c> into
    /// <paramref name="containerPath"/> — a location that is always reachable (the persistent
    /// running-items region). Activates a fresh inner transformer so subsequent streaming chunks
    /// arrive into the newly-placed contents div.
    /// </summary>
    public void ReInsert(string containerPath)
    {
        this.IsInserted = false;
        this.transformer?.Dispose();
        this.transformer = null;
        this.messageSlots.Clear();
        this.sink.UpdateContent(containerPath, ChatOutputUpdateLocation.Append, this.BuildHtml());
        this.IsInserted = true;
        this.Activate();
    }

    public void Dispose() => this.transformer?.Dispose();
}

/// <summary>Transforms the running-items source collection into <see cref="RunningChatItemHtmlModel"/> instances.</summary>
internal sealed class RunningChatItemsHtmlTransformer : CollectionTransformer<AgentChatRunningItem, RunningChatItemHtmlModel>
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<int> nextId;
    private readonly Dictionary<string, RenderSlot> sharedSlotByCallId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;

    public RunningChatItemsHtmlTransformer(
        IReadOnlyList<AgentChatRunningItem> source,
        List<RunningChatItemHtmlModel> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        Dictionary<string, RenderSlot> sharedSlotByCallId,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
        : base(source, target)
    {
        ArgumentNullException.ThrowIfNull(sharedSlotByCallId);
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.nextId = nextId;
        this.sharedSlotByCallId = sharedSlotByCallId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<RunningChatItemHtmlModel> Models => (List<RunningChatItemHtmlModel>)this.Target;

    protected override RunningChatItemHtmlModel Create(AgentChatRunningItem sourceItem)
        => new(
            ChatOutputHtmlRenderer.RunningItemId(this.nextId()),
            sourceItem,
            this.isReasoningVisible,
            this.sink,
            this.sharedSlotByCallId,
            this.toolFactory,
            this.statusSink);

    protected override void Update(RunningChatItemHtmlModel target, AgentChatRunningItem sourceItem)
        => target.Update(sourceItem);

    protected override void OnInsert(int index, RunningChatItemHtmlModel target)
    {
        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.RunningContainerId,
            ChatOutputUpdateLocation.Append,
            target.BuildHtml());
        target.IsInserted = true;
        target.Activate();
    }

    protected override void OnRemoveAt(int index, RunningChatItemHtmlModel target)
        => this.sink.RemoveContent(target.ElementId);
}

/// <summary>
/// Top-level chat-output model for the browser-hosted renderer. Mirrors the selectable-text output
/// model but, instead of building an Avalonia inline tree, emits incremental HTML operations to an
/// <see cref="IChatOutputHtmlSink"/> and requests a scroll-to-bottom after each content change.
/// History is loaded asynchronously in three phases to keep the UI thread responsive:
/// <list type="bullet">
///   <item><description><b>Phase A</b> — subscribe to history/running collections, create the
///   running/sub-agent transformers, and snapshot the history.</description></item>
///   <item><description><b>Phase B</b> — build a complete off-thread render plan for the snapshot,
///   then prepend one HTML blob per chunk (newest first) into the persistent history
///   container.</description></item>
///   <item><description><b>Phase C</b> — publish the plan's slots and call map, then construct the
///   live history transformer, replaying the collection-changed events buffered during loading so
///   every mutation is applied to the DOM exactly once.</description></item>
/// </list>
/// </summary>
public sealed class ChatOutputHtmlModel : IDisposable
{
    /// <summary>Maximum number of history items processed in a single off-thread generation chunk.</summary>
    public const int HistoryChunkSize = 200;

    private readonly IChatOutputHtmlSink sink;
    private readonly IReadOnlyList<AgentChatHistoryItem> historyItems;
    private readonly IReadOnlyList<AgentChatRunningItem> runningItems;
    private readonly Func<bool> isReasoningVisible;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private readonly Action? beforeDispatchHistoryChunk;
    private readonly RunningChatItemsHtmlTransformer runningTransformer;
    private readonly RunningSubAgentsHtmlTransformer? subAgentsTransformer;
    private readonly List<RenderSlot> historySlots = [];
    private readonly List<RunningChatItemHtmlModel> runningModels = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];
    private readonly Dictionary<string, RenderSlot> sharedSlotByCallId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource loadCts = new();
    private ChatMessageHtmlTransformer? historyTransformer;
    private bool historyLoading;
    private List<NotifyCollectionChangedEventArgs>? bufferedHistoryEvents;
    private int idSequence;

    /// <summary>
    /// Task that completes when the history has been fully loaded and the live transformer is ready.
    /// Exposed internally for tests to await before asserting history-related sink state.
    /// </summary>
    internal Task HistoryLoaded { get; }

    /// <summary>Exposed internally for tests that assert publication/cancellation invariants.</summary>
    internal IReadOnlyList<RenderSlot> HistorySlots => this.historySlots;

    /// <summary>Exposed internally for tests that assert publication/cancellation invariants.</summary>
    internal IReadOnlyDictionary<string, RenderSlot> SharedSlotByCallId => this.sharedSlotByCallId;

    public ChatOutputHtmlModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null,
        IReadOnlyList<IRunningSubAgentDisplay>? subAgents = null,
        IReadOnlyList<IRunningSubAgent>? ancestors = null,
        Action? beforeDispatchHistoryChunk = null,
        IRunningSubAgentDisplay? parentAgent = null)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        ArgumentNullException.ThrowIfNull(runningItems);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);

        this.historyItems = historyItems;
        this.runningItems = runningItems;
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;
        this.beforeDispatchHistoryChunk = beforeDispatchHistoryChunk;

        // Phase A: take a snapshot of history for off-thread processing.
        var snapshot = new List<AgentChatHistoryItem>(historyItems);
        this.historyLoading = true;
        this.bufferedHistoryEvents = [];

        // Subscribe before Task.Run so no CollectionChanged events are missed.
        if (historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged += this.OnHistoryCollectionChanged;
        }

        this.runningTransformer = new RunningChatItemsHtmlTransformer(
            runningItems,
            this.runningModels,
            sink,
            isReasoningVisible,
            this.NextId,
            this.sharedSlotByCallId,
            toolFactory,
            statusSink);

        if (subAgents is not null)
        {
            this.subAgentsTransformer = new RunningSubAgentsHtmlTransformer(subAgents, ancestors ?? [], sink, parentAgent);
        }

        // Emit the always-present ancestor breadcrumb for non-root agents (issue #1046).
        // BuildAncestors includes the current agent, so a root agent yields a single-entry
        // chain (just itself); only emit when there is at least one real ancestor above it.
        if (ancestors is { Count: > 1 })
        {
            var ancestorModels = new List<AncestorLinkHtmlModel>(ancestors.Count);
            for (var i = 0; i < ancestors.Count; i++)
            {
                var a = ancestors[i];
                ancestorModels.Add(new AncestorLinkHtmlModel(
                    a.AgentId,
                    a.DisplayName,
                    IsRoot: i == 0,
                    IsCurrent: i == ancestors.Count - 1));
            }

            var breadcrumbHtml = ChatOutputHtmlRenderer.RenderAncestorLinks(ancestorModels);
            if (!string.IsNullOrEmpty(breadcrumbHtml))
            {
                sink.UpdateContent(ChatOutputHtmlRenderer.HistoryContainerId, ChatOutputUpdateLocation.Prepend, breadcrumbHtml);
            }
        }

        if (runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged += this.OnRunningCollectionChanged;
        }

        this.SyncRunningItemSubscriptions();

        // Capture the token before Task.Run so that if Dispose() is called synchronously
        // after construction (before the thread pool lambda starts), the lambda does not
        // throw ObjectDisposedException when accessing loadCts.Token.
        var loadToken = this.loadCts.Token;

        // Fire off background history load; HistoryLoaded completes when Phase C finishes.
        this.HistoryLoaded = Task.Run(() => this.LoadHistoryChunksAsync(snapshot, loadToken));
    }

    /// <summary>
    /// Complete precomputed rendering state for a history snapshot: one slot per item (in snapshot
    /// order), a single call-id lookup spanning the whole snapshot, and newest-first chunk ranges.
    /// Built entirely off-thread before any chunk HTML is generated, so cross-chunk tool-result
    /// matching does not depend on chunk processing order.
    /// </summary>
    internal sealed class HistoryRenderPlan
    {
        public required RenderSlot[] Slots { get; init; }

        public required Dictionary<string, RenderSlot> SlotByCallId { get; init; }

        public required IReadOnlyList<(int Start, int End)> Chunks { get; init; }
    }

    /// <summary>
    /// Builds the full render plan for <paramref name="snapshot"/>: creates one slot per item with
    /// history-index element ids, registers every call id, injects fully-matched result-only
    /// messages into their call models, computes global tool-call groups (ids derived from the
    /// first member's history index), and computes newest-first chunk ranges. Performs no sink
    /// operations; callable off the UI thread.
    /// </summary>
    internal static HistoryRenderPlan BuildHistoryRenderPlan(
        IReadOnlyList<AgentChatHistoryItem> snapshot,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null)
    {
        var slots = new RenderSlot[snapshot.Count];
        var slotByCallId = new Dictionary<string, RenderSlot>(StringComparer.Ordinal);

        // Pass 1: create slots and register call ids across the whole snapshot.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var slot = new RenderSlot(new ChatMessageHtmlModel(
                i,
                ChatOutputHtmlRenderer.MessageId(i),
                snapshot[i],
                isReasoningVisible,
                sink,
                toolFactory,
                statusSink,
                resolveSubAgentId));
            slots[i] = slot;

            foreach (var content in snapshot[i].Contents)
            {
                if (content is FunctionCallContent call && call.CallId is not null)
                {
                    slotByCallId[call.CallId] = slot;
                }
            }
        }

        // Pass 2: suppress diagnostics and inject fully-matched result-only messages; also suppress
        // any message that renders no visible content so it neither emits an empty bubble nor breaks
        // tool-call grouping.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var item = snapshot[i];

            if (!ChatMessageHtmlTransformer.IsToolResultOnlyItem(item))
            {
                if (slots[i].Model.ProducesNoVisibleContent)
                {
                    slots[i].HasDomElement = false;
                }

                continue;
            }

            List<(RenderSlot MatchedSlot, FunctionResultContent Result)>? matches = null;
            var allMatched = true;
            foreach (var content in item.Contents)
            {
                var result = (FunctionResultContent)content;
                if (result.CallId is null || !slotByCallId.TryGetValue(result.CallId, out var matchedSlot))
                {
                    allMatched = false;
                    break;
                }

                (matches ??= []).Add((matchedSlot, result));
            }

            if (!allMatched || matches is null)
            {
                continue;
            }

            foreach (var (matchedSlot, result) in matches)
            {
                matchedSlot.Model.AddSupplementalResult(result);
            }

            slots[i].HasDomElement = false;
        }

        // Pass 3: compute contiguous tool-call groups globally. Group ids use the first member's
        // history index; grouping skips grouping-transparent items (result-only, no-DOM, and
        // no-visible-content messages) but never crosses any displayed non-tool message.
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (!ChatMessageHtmlTransformer.IsToolCallOnlyItem(snapshot[i]))
            {
                continue;
            }

            RenderSlot? groupablePredecessor = null;
            for (var j = i - 1; j >= 0; j--)
            {
                if (ChatMessageHtmlTransformer.IsGroupingTransparent(slots[j]))
                {
                    continue;
                }

                if (slots[j].HasDomElement &&
                    (slots[j].Group is not null || ChatMessageHtmlTransformer.IsToolCallOnlyItem(snapshot[j])))
                {
                    groupablePredecessor = slots[j];
                }

                break;
            }

            if (groupablePredecessor is null)
            {
                continue;
            }

            if (groupablePredecessor.Group is { } existingGroup)
            {
                slots[i].Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                existingGroup.AppendItemStateOnly(slots[i].Model);
                slots[i].Group = existingGroup;
            }
            else
            {
                var firstIndex = groupablePredecessor.Model.SourceIndex;
                groupablePredecessor.Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                slots[i].Model.SetIsInsideMessageLevelToolGroup(true, emit: false);
                var group = new ToolCallGroupHtmlModel(
                    firstIndex,
                    ChatOutputHtmlRenderer.ToolGroupId(firstIndex),
                    sink,
                    groupablePredecessor.Model);
                groupablePredecessor.Group = group;
                groupablePredecessor.IsTopLevelFirstGroupMember = true;
                group.AppendItemStateOnly(slots[i].Model);
                slots[i].Group = group;
            }
        }

        return new HistoryRenderPlan
        {
            Slots = slots,
            SlotByCallId = slotByCallId,
            Chunks = ComputeChunkRanges(snapshot),
        };
    }

    /// <summary>
    /// Generates the HTML blob for plan slots in <c>[start, end)</c>. Slots without a DOM element
    /// are skipped; a group is emitted once (wrapping all member message HTML intact, so later
    /// per-content diffs can target the nested ids); everything else is emitted standalone.
    /// Never invokes <c>OnInsert</c> and never emits sink operations. Callable off the UI thread.
    /// </summary>
    internal static string GenerateHistoryChunk(HistoryRenderPlan plan, int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var slot = plan.Slots[i];
            if (!slot.HasDomElement)
            {
                continue;
            }

            if (slot.Group is { } group)
            {
                slot.Model.IsInserted = true;
                if (!slot.IsTopLevelFirstGroupMember)
                {
                    continue;
                }

                var body = new StringBuilder();
                var postGroup = new StringBuilder();
                for (var j = i; j < plan.Slots.Length; j++)
                {
                    if (ReferenceEquals(plan.Slots[j].Group, group))
                    {
                        body.Append(plan.Slots[j].Model.BuildGroupedMemberHtml());
                        postGroup.Append(plan.Slots[j].Model.BuildGroupedMemberPostGroupHtml());
                        plan.Slots[j].Model.IsInserted = true;
                    }
                    else if (plan.Slots[j].HasDomElement)
                    {
                        break;
                    }
                }

                builder.Append(group.BuildHtml(body.ToString(), postGroup.ToString()));
            }
            else
            {
                builder.Append(slot.Model.BuildHtml());
                slot.Model.IsInserted = true;
            }
        }

        return builder.ToString();
    }

    private async Task LoadHistoryChunksAsync(
        List<AgentChatHistoryItem> snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            // Phase B: build the complete render plan off-thread before any chunk is generated.
            // Nothing is published to this model until every chunk has been delivered.
            var plan = BuildHistoryRenderPlan(
                snapshot,
                this.sink,
                this.isReasoningVisible,
                this.toolFactory,
                this.statusSink,
                this.resolveSubAgentId);

            var scrolled = false;

            // Chunks are ordered newest-first so the user sees the most recent content immediately;
            // each chunk blob is prepended to the persistent history container, causing older chunks
            // to naturally stack in above previously-inserted ones.
            foreach (var (start, end) in plan.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkHtml = GenerateHistoryChunk(plan, start, end);
                this.beforeDispatchHistoryChunk?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (chunkHtml.Length == 0)
                {
                    continue;
                }

                var scrollAfterThisChunk = !scrolled;
                scrolled = true;

                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        this.sink.BeginBatch();
                        this.sink.UpdateContent(
                            ChatOutputHtmlRenderer.HistoryContainerId,
                            ChatOutputUpdateLocation.Prepend,
                            chunkHtml);

                        // Scroll to bottom immediately after the first (newest) chunk, making the most
                        // recent content visible while older chunks fill in above.
                        if (scrollAfterThisChunk)
                        {
                            this.sink.ScrollToBottom();
                        }

                        this.sink.EndBatch();
                    },
                    DispatcherPriority.Normal,
                    cancellationToken);
            }

            // Phase C: publish the plan, then construct the live transformer, replaying the
            // buffered collection-changed events captured during loading so every mutation
            // (adds, replaces, removes, moves) is applied to the DOM exactly once.
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    this.historySlots.AddRange(plan.Slots);
                    foreach (var (callId, slot) in plan.SlotByCallId)
                    {
                        this.sharedSlotByCallId[callId] = slot;
                    }

                    this.historyTransformer = new ChatMessageHtmlTransformer(
                        this.historyItems,
                        this.historySlots,
                        this.sink,
                        this.isReasoningVisible,
                        containerPath: ChatOutputHtmlRenderer.HistoryContainerId,
                        elementIdForSourceIndex: ChatOutputHtmlRenderer.MessageId,
                        groupIdForSourceIndex: ChatOutputHtmlRenderer.ToolGroupId,
                        sharedSlotByCallId: this.sharedSlotByCallId,
                        toolFactory: this.toolFactory,
                        statusSink: this.statusSink,
                        resolveSubAgentId: this.resolveSubAgentId,
                        preloadedCount: snapshot.Count,
                        bufferedEvents: this.bufferedHistoryEvents);

                    this.historyLoading = false;

                    if (this.bufferedHistoryEvents is { Count: > 0 })
                    {
                        this.sink.ScrollToBottom();
                    }

                    this.bufferedHistoryEvents = null;
                },
                DispatcherPriority.Normal,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Disposed before loading completed; the plan is discarded without publishing partial
            // slots or call-map entries.
        }
        finally
        {
            this.loadCts.Dispose();
        }
    }

    /// <summary>Re-renders every message (for example, when reasoning visibility toggles).</summary>
    public void Refresh()
    {
        if (!this.historyLoading)
        {
            foreach (var slot in this.historySlots)
            {
                slot.Model.Refresh();
            }
        }

        foreach (var model in this.runningModels)
        {
            model.Refresh();
        }

        this.sink.ScrollToBottom();
    }

    /// <summary>
    /// Called when the browser reports that a DOM command targeting
    /// <paramref name="failedPath"/> was silently dropped because the element did not exist.
    /// Running items recover by re-appending the affected container into the persistent
    /// running-items region via <see cref="RunningChatItemHtmlModel.ReInsert"/>; history-side
    /// failures recover through <see cref="ChatMessageHtmlTransformer.RepairFailedElement"/>,
    /// which re-emits the affected slot and the rest of the history tail from a still-valid
    /// anchor (or the persistent history container).
    /// </summary>
    public void NotifyInsertionFailed(string failedPath)
    {
        foreach (var model in this.runningModels)
        {
            if (model.ElementId == failedPath ||
                ChatOutputHtmlRenderer.RunningItemContentsId(model.ElementId) == failedPath)
            {
                model.ReInsert(ChatOutputHtmlRenderer.RunningContainerId);
                return;
            }
        }

        this.historyTransformer?.RepairFailedElement(failedPath);
    }

    /// <summary>
    /// Returns a list of <c>(Start, End)</c> index ranges for chunks of <paramref name="snapshot"/>,
    /// newest-first. Each raw cut point (a multiple of <see cref="HistoryChunkSize"/> from the end)
    /// is snapped backward past any contiguous tool-related run it falls inside, ensuring that tool-call
    /// groups and their results are never split across independently generated chunks.
    ///
    /// <para>In pathological cases (e.g. a conversation consisting entirely of tool calls), snapping may
    /// produce a single chunk covering the entire snapshot. This is intentional: the whole run must be
    /// processed together for correct grouping.</para>
    /// </summary>
    internal static IReadOnlyList<(int Start, int End)> ComputeChunkRanges(
        IReadOnlyList<AgentChatHistoryItem> snapshot)
    {
        var chunks = new List<(int Start, int End)>();
        var i = snapshot.Count;
        while (i > 0)
        {
            var rawStart = Math.Max(0, i - HistoryChunkSize);
            var start = SnapCutPoint(snapshot, rawStart);
            chunks.Add((start, i));
            i = start;
        }

        return chunks;
    }

    private static int SnapCutPoint(IReadOnlyList<AgentChatHistoryItem> snapshot, int rawCut)
    {
        var k = rawCut;
        while (k > 0 && IsToolRelated(snapshot[k - 1]))
        {
            k--;
        }

        return k;
    }

    private static bool IsToolRelated(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        var allCalls = true;
        var allResults = true;
        foreach (var content in item.Contents)
        {
            if (content is not FunctionCallContent) { allCalls = false; }
            if (content is not FunctionResultContent) { allResults = false; }
        }

        return allCalls || allResults;
    }

    public void Dispose()
    {
        try
        {
            this.loadCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (this.historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged -= this.OnHistoryCollectionChanged;
        }

        if (this.runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged -= this.OnRunningCollectionChanged;
        }

        foreach (var pair in this.runningItemHandlers)
        {
            pair.Key.Items.CollectionChanged -= pair.Value;
        }

        this.runningItemHandlers.Clear();
        this.subAgentsTransformer?.Dispose();
        this.runningTransformer.Dispose();
        this.historyTransformer?.Dispose();
    }

    private int NextId() => this.idSequence++;

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.historyLoading)
        {
            this.bufferedHistoryEvents?.Add(e);
        }
        else
        {
            this.sink.ScrollToBottom();
        }
    }

    private void OnRunningCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncRunningItemSubscriptions();
        this.sink.ScrollToBottom();
    }

    private void OnRunningItemMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.sink.ScrollToBottom();

    private void SyncRunningItemSubscriptions()
    {
        var removedItems = this.runningItemHandlers.Keys.Except(this.runningItems).ToArray();
        foreach (var removedItem in removedItems)
        {
            removedItem.Items.CollectionChanged -= this.runningItemHandlers[removedItem];
            this.runningItemHandlers.Remove(removedItem);
        }

        foreach (var runningItem in this.runningItems)
        {
            if (runningItem is null)
            {
                continue;
            }

            if (this.runningItemHandlers.ContainsKey(runningItem))
            {
                continue;
            }

            NotifyCollectionChangedEventHandler handler = this.OnRunningItemMessagesChanged;
            runningItem.Items.CollectionChanged += handler;
            this.runningItemHandlers[runningItem] = handler;
        }
    }
}

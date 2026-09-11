using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Common UI-facing chat surface shared by the local engine and the remote proxy
/// (issue #1485). Implementations own their observable collections and mutate them
/// only on their captured foreground scheduler. Deliberately excludes process
/// handles, transports, trust profiles, and MXC objects.
/// </summary>
public interface IAgentChat : IAsyncDisposable, IServiceProvider
{
    AgentInformation Information { get; }
    Usage Usage { get; }
    bool IsBusy { get; }
    AgentChatHistoryCollection History { get; }
    Task HistoryPopulated { get; }
    AgentChatRunningItemCollection RunningItems { get; }
    IAgentInputQueues InputQueues { get; }
    ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; }
    ReadOnlyObservableCollection<AgentChatModal> Modals { get; }
    ISlashCommandRegistry SlashCommands { get; }

    event EventHandler? InformationChanged;
    event EventHandler? ToolsChanged;
    event EventHandler? UsageChanged;
    event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    IReadOnlyList<AgentChatToolItem> GetToolSnapshot();

    Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default);

    Task RespondToModalAsync(
        string modalId, JsonElement response, CancellationToken ct = default);

    void EnqueueSystemNote(string text);
    void EnqueueHelpNote(string text);
    void EnqueueTransientDiagnostic(string text);

    void Interrupt();
}

public interface IAsyncInterruptibleAgentChat
{
    Task InterruptAsync(CancellationToken ct = default);
}

/// <summary>
/// Immutable snapshot of session-level LLM token/cost usage.
/// A metric of <see langword="null"/> means the provider did not report that value;
/// counts must otherwise be nonnegative and cost is a nonnegative USD amount.
/// </summary>
public readonly record struct Usage
{
    public Usage() { }
    public long? TotalInputTokenCount { get; init; } = null;
    public long? TotalOutputTokenCount { get; init; } = null;
    public long? TotalCacheReadTokenCount { get; init; } = null;
    public long? TotalCacheWriteTokenCount { get; init; } = null;
    public long? TotalReasoningTokenCount { get; init; } = null;
    public double? TotalSessionCostUsd { get; init; } = null;
}

/// <summary>
/// Immutable snapshot of the agent-session identity + display information.
/// Every value is applied atomically to <see cref="IAgentChat.Information"/> before
/// <see cref="IAgentChat.InformationChanged"/> is raised.
/// </summary>
public readonly record struct AgentInformation
{
    public AgentInformation() { }
    public required string AgentSessionId { get; init; }
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool AcceptsUserInput { get; init; }
    public string? CurrentModelId { get; init; } = null;
    public required AgentDefinition AgentDefinition { get; init; }
}

/// <summary>
/// An unresolved modal owned by an <see cref="IAgentChat"/>.
/// Modals are immutable value records; a modal is resolved via
/// <see cref="IAgentChat.RespondToModalAsync"/> and removed once the owner
/// dispatches the corresponding dismiss delta.
/// </summary>
public sealed record AgentChatModal
{
    private string id = string.Empty;
    private string ownerAgentId = string.Empty;
    private string title = string.Empty;
    private string body = string.Empty;
    private AgentChatModalContent? content;

    public required string Id
    {
        get => this.id;
        init => this.id = RequireNonBlank(value, nameof(Id));
    }

    public required string OwnerAgentId
    {
        get => this.ownerAgentId;
        init => this.ownerAgentId = RequireNonBlank(value, nameof(OwnerAgentId));
    }

    public required string Title
    {
        get => this.title;
        init => this.title = RequireNonBlank(value, nameof(Title));
    }

    public required string Body
    {
        get => this.body;
        init => this.body = RequireNonBlank(value, nameof(Body));
    }

    public required AgentChatModalContent Content
    {
        get => this.content ?? throw new InvalidOperationException("Modal content was not initialized.");
        init => this.content = value ?? throw new ArgumentNullException(nameof(Content));
    }

    private static string RequireNonBlank(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{propertyName} must be non-blank.", propertyName);
        }

        return value;
    }
}

/// <summary>Discriminated content payload for an <see cref="AgentChatModal"/>.</summary>
public abstract record AgentChatModalContent
{
    public abstract string Type { get; }
}

public sealed record FreeformModalContent : AgentChatModalContent
{
    public override string Type => "freeform";
    public string? Placeholder { get; init; } = null;
    public required bool IsRequired { get; init; }
}

public sealed record MultipleChoiceModalContent : AgentChatModalContent
{
    private readonly System.Collections.Immutable.ImmutableArray<JsonElement> options;

    public override string Type => "multiple-choice";

    /// <summary>
    /// Multiple-choice options. Callers may pass any <see cref="IReadOnlyList{T}"/>; the record
    /// deep-clones each <see cref="JsonElement"/> so later caller mutations of the source
    /// <see cref="JsonDocument"/> cannot affect the published modal, and rejects duplicate options
    /// (issue #1485).
    /// </summary>
    public required IReadOnlyList<JsonElement> Options
    {
        get => this.options;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0)
            {
                throw new ArgumentException("Options must contain at least one entry.", nameof(value));
            }
            var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<JsonElement>(value.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in value)
            {
                var cloned = option.Clone();
                var rawJson = cloned.GetRawText();
                if (!seen.Add(rawJson))
                {
                    throw new ArgumentException(
                        $"Duplicate option '{rawJson}' rejected; multiple-choice options must be distinct.",
                        nameof(value));
                }
                builder.Add(cloned);
            }
            this.options = builder.ToImmutable();
        }
    }

    public required bool AllowsMultiple { get; init; }
}

public sealed record ApprovalModalContent : AgentChatModalContent
{
    public override string Type => "approval";
    private string approveLabel = string.Empty;
    private string rejectLabel = string.Empty;

    public required string ApproveLabel
    {
        get => this.approveLabel;
        init => this.approveLabel = RequireNonBlank(value, nameof(ApproveLabel));
    }

    public required string RejectLabel
    {
        get => this.rejectLabel;
        init => this.rejectLabel = RequireNonBlank(value, nameof(RejectLabel));
    }

    private static string RequireNonBlank(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{propertyName} must be non-blank.", propertyName);
        }

        return value;
    }
}

using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed record AgentInputItem
{
    /// <summary>
    /// Stable owner-authoritative item identifier assigned at enqueue-time (issue #1485). Never blank.
    /// Preserved across edits/moves so the common queue protocol can reference this item by id
    /// rather than by fragile list index.
    /// </summary>
    public string ItemId { get; init; } = Guid.NewGuid().ToString("n");

    public required ChatMessage[] Messages { get; init; }

    public AgentChatSession? ResetSession { get; init; }

    internal AgentInputTurnCompletion? TurnCompletion { get; init; }

    public IReadOnlyList<AIContent> Contents => this.Messages?.SelectMany(m => m.Contents).ToArray() ?? Array.Empty<AIContent>();

    public string Text => string.Concat(
        this.Messages.SelectMany(message => message.Contents).Select(FormatContentAsText));

    private static string FormatContentAsText(AIContent content) => content switch
    {
        TextContent textContent => textContent.Text,
        DataContent dataContent when !string.IsNullOrWhiteSpace(dataContent.MediaType) => $"[{dataContent.MediaType}]",
        DataContent => "[data]",
        UriContent uriContent when !string.IsNullOrWhiteSpace(uriContent.MediaType) => $"[{uriContent.MediaType}] {uriContent.Uri}",
        UriContent uriContent => uriContent.Uri.ToString(),
        _ => $"[{content.GetType().Name}]",
    };
}

internal sealed class AgentInputTurnCompletion
{
    private readonly TaskCompletionSource<Task> completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentInputTurnCompletion()
    {
        Completion = completionSource.Task.Unwrap();
    }

    public Task Completion { get; }

    public void CompleteAfter(Task providerCleanup)
    {
        ArgumentNullException.ThrowIfNull(providerCleanup);
        if (!completionSource.TrySetResult(providerCleanup))
        {
            throw new InvalidOperationException("The agent input turn was completed more than once.");
        }
    }
}

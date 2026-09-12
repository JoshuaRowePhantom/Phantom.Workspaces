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
    private readonly TaskCompletionSource terminalSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource settlementSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource providerReadAbandonedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int terminalClaimed;
    private int completionStarted;

    public Task Terminal => terminalSource.Task;

    public Task Settlement => settlementSource.Task;

    public Task ProviderReadAbandoned => providerReadAbandonedSource.Task;

    public void MarkProviderReadAbandoned()
    {
        providerReadAbandonedSource.TrySetResult();
    }

    public void ClaimTerminal()
    {
        if (Interlocked.Exchange(ref terminalClaimed, 1) != 0)
        {
            throw new InvalidOperationException("The agent input turn terminal was claimed more than once.");
        }

        terminalSource.SetResult();
    }

    public void CompleteAfter(Task providerCleanup)
    {
        ArgumentNullException.ThrowIfNull(providerCleanup);
        if (Volatile.Read(ref terminalClaimed) == 0)
        {
            throw new InvalidOperationException("The agent input turn terminal must be claimed before settlement.");
        }

        if (Interlocked.Exchange(ref completionStarted, 1) != 0)
        {
            throw new InvalidOperationException("The agent input turn was completed more than once.");
        }

        _ = SettleAfterCleanupAsync(providerCleanup);
    }

    private async Task SettleAfterCleanupAsync(Task providerCleanup)
    {
        await providerCleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (providerCleanup.IsFaulted)
        {
            settlementSource.SetException(providerCleanup.Exception!.InnerExceptions);
        }
        else if (providerCleanup.IsCanceled)
        {
            settlementSource.SetCanceled();
        }
        else
        {
            settlementSource.SetResult();
        }
    }
}

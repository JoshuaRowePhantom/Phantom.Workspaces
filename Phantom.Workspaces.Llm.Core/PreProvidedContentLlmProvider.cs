using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

public sealed class PreProvidedContentLlmProvider : ILlmProvider
{
    private const int PrefixPollMilliseconds = 50;

    private readonly ILlmProvider underlyingProvider;
    private readonly object syncLock = new();
    private ImmutableList<LlmEvent> preProvidedContent;
    private long version;

    public PreProvidedContentLlmProvider(
        ILlmProvider underlyingProvider,
        IEnumerable<LlmEvent>? preProvidedContent = null)
    {
        this.underlyingProvider = underlyingProvider;
        this.preProvidedContent = preProvidedContent?.ToImmutableList() ?? ImmutableList<LlmEvent>.Empty;
    }

    public void UpdatePreProvidedContent(
        ImmutableList<LlmEvent> content)
    {
        lock (this.syncLock)
        {
            this.preProvidedContent = content;
            this.version++;
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (prefix, _) = this.GetPrefixSnapshot();
        var prefixedConversation = this.ApplyPrefix(conversation, ImmutableList<LlmEvent>.Empty, prefix);
        await foreach (var streamEvent in this.underlyingProvider
                           .StreamAsync(prefixedConversation, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public async IAsyncEnumerable<LlmConversation> StreamConversationsAsync(
        LlmConversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (activePrefix, activeVersion) = this.GetPrefixSnapshot();
        var currentConversation = this.ApplyPrefix(conversation, ImmutableList<LlmEvent>.Empty, activePrefix);
        yield return currentConversation;

        await using var enumerator = this.underlyingProvider
            .StreamAsync(currentConversation, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        Task<bool>? moveNextTask = null;
        while (true)
        {
            moveNextTask ??= enumerator.MoveNextAsync().AsTask();
            var delayTask = Task.Delay(PrefixPollMilliseconds, cancellationToken);
            var completedTask = await Task.WhenAny(moveNextTask, delayTask);

            var (latestPrefix, latestVersion) = this.GetPrefixSnapshot();
            if (latestVersion != activeVersion)
            {
                currentConversation = this.ApplyPrefix(currentConversation, activePrefix, latestPrefix);
                activePrefix = latestPrefix;
                activeVersion = latestVersion;
                yield return currentConversation;
            }

            if (completedTask != moveNextTask)
            {
                continue;
            }

            if (!await moveNextTask)
            {
                yield break;
            }

            var streamEvent = enumerator.Current;
            moveNextTask = null;

            currentConversation = LlmConversationBuilder
                .FromConversation(currentConversation)
                .AddStreamEvent(streamEvent)
                .Build();

            if (streamEvent.Checkpoint is not null)
            {
                currentConversation = this.ApplyPrefix(
                    currentConversation,
                    ImmutableList<LlmEvent>.Empty,
                    activePrefix);
            }

            yield return currentConversation;
        }
    }

    private (ImmutableList<LlmEvent> Prefix, long Version) GetPrefixSnapshot()
    {
        lock (this.syncLock)
        {
            return (this.preProvidedContent, this.version);
        }
    }

    private LlmConversation ApplyPrefix(
        LlmConversation conversation,
        ImmutableList<LlmEvent> previousPrefix,
        ImmutableList<LlmEvent> newPrefix)
    {
        var remainingEvents = conversation.Events;
        if (previousPrefix.Count > 0 && StartsWith(remainingEvents, previousPrefix))
        {
            remainingEvents = remainingEvents.RemoveRange(0, previousPrefix.Count);
        }

        if (newPrefix.Count > 0)
        {
            remainingEvents = remainingEvents
                .Where(eventItem => !newPrefix.Contains(eventItem))
                .ToImmutableList();
        }

        if (newPrefix.Count > 0 && !StartsWith(remainingEvents, newPrefix))
        {
            remainingEvents = newPrefix.AddRange(remainingEvents);
        }

        if (conversation.Events.SequenceEqual(remainingEvents))
        {
            return conversation;
        }

        return LlmConversation.Create(
            remainingEvents,
            conversation.CreatedAt,
            conversation.UpdatedAt);
    }

    private static bool StartsWith(
        ImmutableList<LlmEvent> events,
        ImmutableList<LlmEvent> prefix)
    {
        if (prefix.Count == 0)
        {
            return true;
        }

        if (events.Count < prefix.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (events[index] != prefix[index])
            {
                return false;
            }
        }

        return true;
    }
}

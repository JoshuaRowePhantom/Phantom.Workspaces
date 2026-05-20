using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class PreProvidedContentLlmProviderTests
{
    [Fact]
    public async Task StreamConversationsAsync_PrefixesGeneratedConversation()
    {
        var source = Channel.CreateUnbounded<LlmStreamEvent>();
        LlmConversation? recordedConversation = null;
        var fakeProvider = new DelegateLlmProvider(
            conversation =>
            {
                recordedConversation = conversation;
                return ReadChannelAsync(source.Reader);
            });
        var prefix = ImmutableList.Create(
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.System,
                Content = "prefix:",
            });
        var provider = new PreProvidedContentLlmProvider(fakeProvider, prefix);
        var inputConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "hello",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("prefix:", enumerator.Current.Events[0].Content);
        Assert.Equal("hello", enumerator.Current.Events[1].Content);
        Assert.Null(recordedConversation);
        source.Writer.TryComplete();
    }

    [Fact]
    public async Task StreamAsync_PrefixesConversationPassedToUnderlyingProvider()
    {
        LlmConversation? recordedConversation = null;
        var fakeProvider = new DelegateLlmProvider(
            conversation =>
            {
                recordedConversation = conversation;
                return GetEmptyEventsAsync();
            });
        var provider = new PreProvidedContentLlmProvider(
            fakeProvider,
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.System,
                    Content = "prefix",
                },
            ]);

        await foreach (var _ in provider.StreamAsync(
                           LlmConversation.Create(
                               events:
                               [
                                   new LlmEvent
                                   {
                                       EventKind = LlmEventKinds.Turn,
                                       Role = LlmRoles.User,
                                       Content = "u",
                                   },
                               ])))
        {
        }

        Assert.NotNull(recordedConversation);
        Assert.Equal("prefix", recordedConversation!.Events[0].Content);
        Assert.Equal("u", recordedConversation.Events[1].Content);
    }

    [Fact]
    public async Task StreamConversationsAsync_DoesNotDuplicatePrefix()
    {
        var fakeProvider = new DelegateLlmProvider(_ => GetEmptyEventsAsync());
        var prefix = ImmutableList.Create(
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.System,
                Content = "prefix",
            });
        var provider = new PreProvidedContentLlmProvider(fakeProvider, prefix);
        var inputConversation = LlmConversation.Create(
            events:
            [
                prefix[0],
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "hello",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.Events.Count);
        Assert.Equal("prefix", enumerator.Current.Events[0].Content);
        Assert.Equal("hello", enumerator.Current.Events[1].Content);
    }

    [Fact]
    public async Task StreamConversationsAsync_RemovesDuplicatePrefixEventsFromBody()
    {
        var fakeProvider = new DelegateLlmProvider(_ => GetEmptyEventsAsync());
        var prefixEvent = new LlmEvent
        {
            EventKind = LlmEventKinds.Turn,
            Role = LlmRoles.System,
            Content = "prefix",
        };
        var prefix = ImmutableList.Create(prefixEvent);
        var provider = new PreProvidedContentLlmProvider(fakeProvider, prefix);
        var inputConversation = LlmConversation.Create(
            events:
            [
                prefixEvent,
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "hello",
                },
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.System,
                    Content = "prefix",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.Events.Count);
        Assert.Equal("prefix", enumerator.Current.Events[0].Content);
        Assert.Equal("hello", enumerator.Current.Events[1].Content);
    }

    [Fact]
    public async Task StreamConversationsAsync_WhenPreProvidedContentChanges_StreamsUpdatedConversation()
    {
        var source = Channel.CreateUnbounded<LlmStreamEvent>();
        var fakeProvider = new DelegateLlmProvider(_ => ReadChannelAsync(source.Reader));
        var initialPrefix = ImmutableList.Create(
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.System,
                Content = "a",
            });
        var provider = new PreProvidedContentLlmProvider(fakeProvider, initialPrefix);
        var inputConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "hello",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("a", enumerator.Current.Events[0].Content);

        provider.UpdatePreProvidedContent(
            ImmutableList.Create(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.System,
                    Content = "b",
                }));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("b", enumerator.Current.Events[0].Content);
        Assert.Equal("hello", enumerator.Current.Events[1].Content);

        source.Writer.TryComplete();
    }

    [Fact]
    public async Task StreamConversationsAsync_AppliesReplaceEvents()
    {
        var source = Channel.CreateUnbounded<LlmStreamEvent>();
        var fakeProvider = new DelegateLlmProvider(_ => ReadChannelAsync(source.Reader));
        var provider = new PreProvidedContentLlmProvider(fakeProvider);
        var inputConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "start",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("start", enumerator.Current.Events[0].Content);

        source.Writer.TryWrite(
            new LlmStreamEvent
            {
                Replace = new LlmReplaceEvent
                {
                    RemoveCount = 1,
                    Events =
                    [
                        new LlmEvent
                        {
                            EventKind = LlmEventKinds.Turn,
                            Role = LlmRoles.User,
                            Content = "replacement",
                        },
                    ],
                },
            });

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Single(enumerator.Current.Events);
        Assert.Equal("replacement", enumerator.Current.Events[0].Content);
        source.Writer.TryComplete();
    }

    [Fact]
    public async Task StreamConversationsAsync_YieldsCheckpointConversation()
    {
        var source = Channel.CreateUnbounded<LlmStreamEvent>();
        var fakeProvider = new DelegateLlmProvider(_ => ReadChannelAsync(source.Reader));
        var provider = new PreProvidedContentLlmProvider(fakeProvider);
        var checkpointConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.System,
                    Content = "new conversation",
                },
            ]);
        var inputConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "start",
                },
            ]);

        await using var enumerator = provider.StreamConversationsAsync(inputConversation).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("start", enumerator.Current.Events[0].Content);

        source.Writer.TryWrite(
            new LlmStreamEvent
            {
                Checkpoint = new LlmCheckpointEvent
                {
                    Conversation = checkpointConversation,
                },
            });

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Single(enumerator.Current.Events);
        Assert.Equal("new conversation", enumerator.Current.Events[0].Content);
        source.Writer.TryComplete();
    }

    private static async IAsyncEnumerable<LlmStreamEvent> GetEmptyEventsAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<LlmStreamEvent> ReadChannelAsync(
        ChannelReader<LlmStreamEvent> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var llmEvent))
            {
                yield return llmEvent;
            }
        }
    }

    private sealed class DelegateLlmProvider(
        Func<LlmConversation, IAsyncEnumerable<LlmStreamEvent>> stream)
        : ILlmProvider
    {
        private readonly Func<LlmConversation, IAsyncEnumerable<LlmStreamEvent>> stream = stream;

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmConversation conversation,
            CancellationToken cancellationToken = default)
        {
            return this.stream(conversation);
        }
    }
}

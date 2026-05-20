namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public async Task Process_WhenInputArrives_AppendsInputAndProviderStream()
    {
        var session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            new TestProvider(TestProvider.Content("assistant reply")));
        var updates = new List<AgentSessionUpdate>();

        await foreach (var update in session.Process(GetInputs()))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Null(updates[0].LlmStreamingEvent);
        Assert.NotNull(updates[1].LlmStreamingEvent);
        Assert.Equal("assistant reply", updates[1].LlmStreamingEvent?.Event?.Content);
        var events = updates[^1].LlmSession.Conversations[^1].Events;
        Assert.Equal(2, events.Count);
        Assert.Equal("hello", events[0].Content);
        Assert.Equal("assistant reply", events[1].Content);
    }

    [Fact]
    public async Task Process_WhenInterruptInputArrives_CancelsCurrentProviderAndRestarts()
    {
        var provider = new InterruptibleProvider();
        var session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            provider);
        var updates = new List<AgentSessionUpdate>();

        await foreach (var update in session.Process(GetInterruptingInputs()))
        {
            updates.Add(update);
            if (updates.Count >= 4)
            {
                break;
            }
        }

        Assert.Equal(2, provider.InvocationCount);
        var streamContents = updates
            .Where(static update => update.LlmStreamingEvent?.Event?.Content is not null)
            .Select(static update => update.LlmStreamingEvent!.Event!.Content!)
            .ToArray();
        Assert.Contains("stream-1", streamContents);
        Assert.Contains("stream-2", streamContents);
    }

    private static async IAsyncEnumerable<SessionInputEvent> GetInputs()
    {
        yield return new SessionInputEvent
        {
            LlmEvents =
            [
                TestProvider.UserTurn("hello"),
            ],
        };

        await Task.Yield();
    }

    private static async IAsyncEnumerable<SessionInputEvent> GetInterruptingInputs()
    {
        yield return new SessionInputEvent
        {
            LlmEvents =
            [
                TestProvider.UserTurn("first"),
            ],
        };

        await Task.Delay(20);

        yield return new SessionInputEvent
        {
            InterruptCurrentResponse = true,
            LlmEvents =
            [
                TestProvider.UserTurn("second"),
            ],
        };
    }

    private sealed class InterruptibleProvider : ILlmProvider
    {
        private int invocationCount;

        public int InvocationCount => this.invocationCount;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmConversation conversation,
            CancellationToken cancellationToken = default)
        {
            var invocation = Interlocked.Increment(ref this.invocationCount);
            yield return TestProvider.ContentToken($"stream-{invocation}");

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}

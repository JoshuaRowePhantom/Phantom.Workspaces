using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.AI;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class CopilotSubAgentChatClientTests
{
    private static CopilotSubAgentChatClient CreateClient() => new();

    private static ChatResponseUpdate TextUpdate(string text) =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    [Fact]
    public async Task Push_EnqueuesUpdate_DeliveredByCompleteAsync()
    {
        var sut = CreateClient();
        var update1 = TextUpdate("hello");
        var update2 = TextUpdate("world");

        sut.Push(update1);
        sut.Push(update2);
        sut.Complete();

        var results = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync([]))
            results.Add(u);

        Assert.Equal([update1, update2], results);
    }

    [Fact]
    public async Task Push_BeforeCompleteAsync_AllUpdatesDelivered()
    {
        var sut = CreateClient();
        var update = TextUpdate("pre-pushed");

        sut.Push(update);
        sut.Complete();

        var results = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync([]))
            results.Add(u);

        Assert.Single(results);
        Assert.Same(update, results[0]);
    }

    [Fact]
    public async Task Complete_CausesCompleteAsyncToReturn()
    {
        var sut = CreateClient();
        sut.Complete();

        var count = 0;
        await foreach (var _ in sut.GetStreamingResponseAsync([]))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Fail_CausesCompleteAsyncToThrow()
    {
        var sut = CreateClient();
        var ex = new InvalidOperationException("boom");
        sut.Fail(ex);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync([]))
            {
            }
        });

        Assert.Same(ex, thrown);
    }

    [Fact]
    public async Task CompleteAsync_BlocksUntilCompleteOrFail()
    {
        var sut = CreateClient();

        var streamTask = Task.Run(async () =>
        {
            var items = new List<ChatResponseUpdate>();
            await foreach (var u in sut.GetStreamingResponseAsync([]))
                items.Add(u);
            return items;
        });

        // Should not be done yet
        Assert.False(streamTask.IsCompleted);

        sut.Push(TextUpdate("one"));
        sut.Complete();

        var results = await streamTask;
        Assert.Single(results);
    }

    [Fact]
    public void Complete_CalledTwice_IsSafe()
    {
        var sut = CreateClient();
        sut.Complete();
        var ex = Record.Exception(() => sut.Complete());
        Assert.Null(ex);
    }

    [Fact]
    public void Fail_CalledAfterComplete_IsSafe()
    {
        var sut = CreateClient();
        sut.Complete();
        var ex = Record.Exception(() => sut.Fail(new InvalidOperationException("late fail")));
        Assert.Null(ex);
    }

    [Fact]
    public async Task CompleteAsync_CancellationToken_CancelsWait()
    {
        var sut = CreateClient();
        using var cts = new CancellationTokenSource();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync([], cancellationToken: cts.Token))
            {
            }
        });

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamTask);
    }

    [Fact]
    public void GetService_ICopilotSubAgentReceiver_ReturnsSelf()
    {
        var sut = CreateClient();
        var receiver = ((IChatClient)sut).GetService<ICopilotSubAgentReceiver>();
        Assert.Same(sut, receiver);
    }

    [Fact]
    public void GetService_ReturnsNullForOtherTypes()
    {
        var sut = CreateClient();
        Assert.Null(((IChatClient)sut).GetService<IDisposable>());
    }

    [Fact]
    public void GetResponseAsync_ThrowsNotSupportedException()
    {
        var sut = CreateClient();
        var ex = Record.Exception(() => { _ = sut.GetResponseAsync([]); });
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void IsHostedAgentChatClient()
    {
        var sut = CreateClient();
        Assert.IsAssignableFrom<IHostedAgentChatClient>(sut);
    }

    [Fact]
    public void AgentFactory_GithubCopilotSubagentProvider_CreatesCopilotSubAgentChatClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "subagent",
              "model": {
                "id": "github-copilot-subagent",
                "provider": "github-copilot-subagent"
              },
              "tools": []
            }
            """);

        var result = AgentFactory.CreateChatClient(agent);

        Assert.IsType<CopilotSubAgentChatClient>(result.ChatClient);
    }
}

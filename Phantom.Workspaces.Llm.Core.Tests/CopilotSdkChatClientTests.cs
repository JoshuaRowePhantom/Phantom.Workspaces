using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkChatClientTests
{
    [Fact]
    public async Task RunStreamingTurn_OnCancellation_AbortsTheTurn_AndReleasesTheLock()
    {
        // Reproduces GitHub issue #21: cancelling a Copilot turn must actually abort the in-flight
        // CLI turn (not merely abandon the local read), and it must release the turn lock so the next
        // turn can run. Before the fix, cancellation never invoked an abort and a subscription created
        // outside the try/finally could leak the lock, deadlocking every later turn.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, "first"));

        var onCancelledInvoked = false;
        var subscription = new FlagDisposable();
        using var cancellation = new CancellationTokenSource();

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _) =>
            Task.FromResult(new StreamingTurnContext(
                channel.Reader,
                subscription,
                _ => Task.CompletedTask,
                () =>
                {
                    onCancelledInvoked = true;
                    return Task.CompletedTask;
                }));

        await using (var enumerator = client.RunStreamingTurnAsync(BeginTurnAsync, cancellation.Token)
                         .GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }

        Assert.True(onCancelledInvoked);
        Assert.True(subscription.Disposed);

        // The lock must have been released: a second turn should be able to acquire it and reach its
        // begin delegate. The 30s WaitAsync is a deadlock failsafe, not a timing assertion - it only
        // fires if the lock leaked.
        var secondTurnReachedBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedChannel = Channel.CreateUnbounded<ChatResponseUpdate>();
        completedChannel.Writer.Complete();

        Task<StreamingTurnContext> SecondBeginAsync(CancellationToken _)
        {
            secondTurnReachedBegin.TrySetResult();
            return Task.FromResult(new StreamingTurnContext(
                completedChannel.Reader,
                new FlagDisposable(),
                _ => Task.CompletedTask,
                () => Task.CompletedTask));
        }

        var secondTurn = Task.Run(async () =>
        {
            await foreach (var _ in client.RunStreamingTurnAsync(SecondBeginAsync, CancellationToken.None))
            {
            }
        });

        await secondTurn.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(secondTurnReachedBegin.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunStreamingTurn_OnNormalCompletion_DoesNotAbort_AndDisposesSubscription()
    {
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, "only"));
        channel.Writer.Complete();

        var onCancelledInvoked = false;
        var subscription = new FlagDisposable();

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _) =>
            Task.FromResult(new StreamingTurnContext(
                channel.Reader,
                subscription,
                _ => Task.CompletedTask,
                () =>
                {
                    onCancelledInvoked = true;
                    return Task.CompletedTask;
                }));

        var received = 0;
        await foreach (var _ in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
        {
            received++;
        }

        Assert.Equal(1, received);
        Assert.False(onCancelledInvoked);
        Assert.True(subscription.Disposed);
    }

    private sealed class FlagDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }

    [Fact]
    public void Construction_DoesNotStartProcess_AndExposesDisplayName()
    {
        // Constructing the adapter must be lazy: no Copilot CLI process is started until a
        // request is made, so this is safe without authentication.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        Assert.Equal("GitHub Copilot (gpt-5)", client.DisplayName);
    }

    [Fact]
    public void GetService_ReturnsSelf_ForChatClientType()
    {
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(client, client.GetService(typeof(CopilotSdkChatClient)));
        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(IChatClient), serviceKey: "key"));
    }

    [Fact]
    public void Dispose_IsSafe_WhenNeverStarted()
    {
        var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void BuildSessionConfig_ForwardsFunctionToolsInstructionsAndModel()
    {
        var tool = AIFunctionFactory.Create(
            (string id) => id,
            "lookup_issue",
            "Fetch issue details from our tracker");
        var options = new ChatOptions
        {
            Instructions = "system prompt",
            Tools = [tool],
        };

        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("gpt-test", config.Model);
        Assert.Equal("system prompt", config.SystemMessage!.Content);
        Assert.NotNull(config.Tools);
        Assert.Contains(config.Tools!, candidate => candidate.Name == "lookup_issue");
    }

    [Fact]
    public void BuildSessionConfig_IgnoresNonFunctionToolsAndMissingOptions()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Equal("gpt-test", config.Model);
        Assert.True(config.Tools is null || config.Tools.Count == 0);
    }

    [Fact]
    public void BuildResumeSessionConfig_ForwardsFunctionToolsInstructionsAndModel()
    {
        var tool = AIFunctionFactory.Create(
            (string id) => id,
            "lookup_issue",
            "Fetch issue details from our tracker");
        var options = new ChatOptions
        {
            Instructions = "system prompt",
            Tools = [tool],
        };

        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("gpt-test", config.Model);
        Assert.Equal("system prompt", config.SystemMessage!.Content);
        Assert.NotNull(config.Tools);
        Assert.Contains(config.Tools!, candidate => candidate.Name == "lookup_issue");
    }

    [Fact]
    public void BuildResumeSessionConfig_IgnoresNonFunctionToolsAndMissingOptions()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Equal("gpt-test", config.Model);
        Assert.True(config.Tools is null || config.Tools.Count == 0);
    }

    [Fact]
    public void BuildResumeSessionConfig_MapsReasoningEffort()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig(
            "gpt-test",
            byokOptions: null,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });

        Assert.Equal("high", config.ReasoningEffort);
    }

    [Fact]
    public void ComputeSessionSignature_IsStableForEquivalentOptions_IgnoringToolOrder()
    {
        var toolA = AIFunctionFactory.Create((string id) => id, "alpha", "a");
        var toolB = AIFunctionFactory.Create((string id) => id, "beta", "b");

        var first = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Instructions = "system",
            Tools = [toolA, toolB],
        });
        var second = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Instructions = "system",
            Tools = [toolB, toolA],
        });

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenToolSetChanges()
    {
        var toolA = AIFunctionFactory.Create((string id) => id, "alpha", "a");
        var toolB = AIFunctionFactory.Create((string id) => id, "beta", "b");

        var withOne = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Tools = [toolA] });
        var withTwo = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Tools = [toolA, toolB] });

        Assert.NotEqual(withOne, withTwo);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenInstructionsChange()
    {
        var first = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Instructions = "one" });
        var second = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Instructions = "two" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenReasoningEffortChanges()
    {
        var low = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        });
        var high = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        });

        Assert.NotEqual(low, high);
    }

    [Fact]
    public void ComputeSessionSignature_TreatsNullAndEmptyOptionsAsEquivalent()
    {
        var fromNull = CopilotSdkChatClient.ComputeSessionSignature(null);
        var fromEmpty = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions());

        Assert.Equal(fromNull, fromEmpty);
    }

    [Fact]
    public void BuildMessageOptions_WithTextOnly_SetsPromptAndNoAttachments()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("hello", result.Prompt);
        Assert.Null(result.Attachments);
    }

    [Fact]
    public void BuildMessageOptions_WithImageAttachment_PopulatesAttachmentsAsBlobAndPreservesText()
    {
        var pngBytes = new byte[] { 1, 2, 3 };
        var contents = new AIContent[]
        {
            new TextContent("describe this"),
            new DataContent(pngBytes, "image/png"),
        };
        var messages = new[] { new ChatMessage(ChatRole.User, contents) };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("describe this", result.Prompt);
        Assert.NotNull(result.Attachments);
        Assert.Single(result.Attachments);
        var blob = Assert.IsType<GitHub.Copilot.SDK.UserMessageAttachmentBlob>(result.Attachments[0]);
        Assert.Equal("image/png", blob.MimeType);
        Assert.Equal(Convert.ToBase64String(pngBytes), blob.Data);
    }

    [Fact]
    public void BuildMessageOptions_WithMultipleImageAttachments_PopulatesAllBlobs()
    {
        var contents = new AIContent[]
        {
            new TextContent("look"),
            new DataContent(new byte[] { 1 }, "image/png"),
            new DataContent(new byte[] { 2 }, "image/jpeg"),
        };
        var messages = new[] { new ChatMessage(ChatRole.User, contents) };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal(2, result.Attachments?.Count);
    }

    [Fact]
    public void BuildMessageOptions_WithImageButNoText_SetsEmptyPromptAndPopulatesAttachments()
    {
        var contents = new AIContent[] { new DataContent(new byte[] { 1 }, "image/png") };
        var messages = new[] { new ChatMessage(ChatRole.User, contents) };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal(string.Empty, result.Prompt);
        Assert.NotNull(result.Attachments);
        Assert.Single(result.Attachments);
    }

    [Fact]
    public void BuildMessageOptions_PicksLastUserMessage_IgnoresEarlierTurns()
    {
        var pngBytes = new byte[] { 9 };
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "ok"),
            new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent("second"),
                new DataContent(pngBytes, "image/png"),
            }),
        };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("second", result.Prompt);
        Assert.Single(result.Attachments!);
    }

    [Fact]
    public void BuildMessageOptions_IgnoresNonImageDataContent_StillPopulatesBlob()
    {
        var pdfBytes = new byte[] { 5, 6 };
        var contents = new AIContent[]
        {
            new TextContent("here"),
            new DataContent(pdfBytes, "application/pdf"),
        };
        var messages = new[] { new ChatMessage(ChatRole.User, contents) };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Single(result.Attachments!);
        var blob = Assert.IsType<GitHub.Copilot.SDK.UserMessageAttachmentBlob>(result.Attachments![0]);
        Assert.Equal("application/pdf", blob.MimeType);
    }
}

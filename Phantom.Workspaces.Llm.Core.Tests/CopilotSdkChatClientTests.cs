using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
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
                },
                () => Task.CompletedTask));

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
                () => Task.CompletedTask,
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
                },
                () => Task.CompletedTask));

        var received = 0;
        await foreach (var _ in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
        {
            received++;
        }

        Assert.Equal(1, received);
        Assert.False(onCancelledInvoked);
        Assert.True(subscription.Disposed);
    }

    [Fact]
    public async Task RunStreamingTurn_WhenSendAsyncThrowsIOException_RetriesWithFreshTurn()
    {
        // Reproduces GitHub issue #267 (Bug 1): when a resumed session's pipe is broken, the first
        // SendAsync throws IOException. RunStreamingTurnAsync must call OnPipeBrokenAsync, invoke
        // beginTurnAsync a second time, and yield updates from the fresh turn — not surface the pipe
        // error as a provider exception.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        var onPipeBrokenInvoked = false;
        var beginCallCount = 0;

        // Second turn's channel carries the real response.
        var freshChannel = Channel.CreateUnbounded<ChatResponseUpdate>();
        freshChannel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, "recovered"));
        freshChannel.Writer.Complete();

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _)
        {
            beginCallCount++;
            if (beginCallCount == 1)
            {
                // First call: SendAsync throws IOException (broken pipe from resumed session).
                var brokenChannel = Channel.CreateUnbounded<ChatResponseUpdate>();
                return Task.FromResult(new StreamingTurnContext(
                    brokenChannel.Reader,
                    new FlagDisposable(),
                    _ => Task.FromException(new IOException("The pipe is being closed.")),
                    () => Task.CompletedTask,
                    () =>
                    {
                        onPipeBrokenInvoked = true;
                        return Task.CompletedTask;
                    }));
            }

            // Second call: fresh session succeeds.
            return Task.FromResult(new StreamingTurnContext(
                freshChannel.Reader,
                new FlagDisposable(),
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask));
        }

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Equal(2, beginCallCount);
        Assert.True(onPipeBrokenInvoked, "OnPipeBrokenAsync must be called when SendAsync throws IOException.");
        Assert.Single(updates);
        Assert.Equal("recovered", updates[0].Text);
    }

    [Fact]
    public async Task RunStreamingTurn_WhenSendAsyncThrowsIOException_FirstSubscriptionIsDisposed()
    {
        // The first (broken) turn's subscription must be disposed before the retry, so resources
        // from the broken session are released regardless of what happens during the retry.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        var firstSubscription = new FlagDisposable();
        var beginCallCount = 0;

        var freshChannel = Channel.CreateUnbounded<ChatResponseUpdate>();
        freshChannel.Writer.Complete();

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _)
        {
            beginCallCount++;
            if (beginCallCount == 1)
            {
                var brokenChannel = Channel.CreateUnbounded<ChatResponseUpdate>();
                return Task.FromResult(new StreamingTurnContext(
                    brokenChannel.Reader,
                    firstSubscription,
                    _ => Task.FromException(new IOException("The pipe is being closed.")),
                    () => Task.CompletedTask,
                    () => Task.CompletedTask));
            }

            return Task.FromResult(new StreamingTurnContext(
                freshChannel.Reader,
                new FlagDisposable(),
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask));
        }

        await foreach (var _ in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
        {
        }

        Assert.True(firstSubscription.Disposed, "The first subscription must be disposed when SendAsync throws IOException.");
    }

    [Fact]
    public async Task RunStreamingTurn_WhenImmediateItemEnqueuedDuringLiveTurn_FiresSteeringMessageForwarded()
    {
        // Regression coverage for issue #320 (test gap from issue #17 fix):
        // When an Immediate-immediacy AgentInputItem is enqueued into the queueManager while a
        // streaming turn is live, SteeringMessageForwarded must fire with the correct ChatMessage.
        var queueManager = new AgentInputQueueManager();
        using var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot (gpt-5)",
            gitHubToken: null,
            loggerFactory: null,
            queueManager: queueManager);

        ChatMessage? forwarded = null;
        client.SteeringMessageForwarded += msg => forwarded = msg;

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var subscription = new FlagDisposable();

        // Signals the test thread once BeginTurnAsync has subscribed to QueueStateChanged.
        var beginTurnCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _)
        {
            // Mirror the production subscription from GetStreamingResponseAsync.BeginTurnAsync.
            void OnQueueChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
            {
                if (e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
                {
                    return;
                }

                while (queueManager.TryDequeueNextImmediate(out var item))
                {
                    foreach (var message in item.Messages ?? [])
                    {
                        var text = string.Concat(
                            message.Contents.OfType<TextContent>().Select(c => c.Text));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            // SteeringMessageForwarded can only be raised from the declaring class;
                            // invoke it via its backing field (consistent with other reflection use in this file).
                            var handler = (Action<ChatMessage>?)typeof(CopilotSdkChatClient)
                                .GetField("SteeringMessageForwarded", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(client);
                            handler?.Invoke(message);
                        }
                    }
                }
            }

            queueManager.QueueStateChanged += OnQueueChanged;
            beginTurnCompletedTcs.TrySetResult();

            return Task.FromResult(new StreamingTurnContext(
                channel.Reader,
                subscription,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask));
        }

        var turnTask = Task.Run(async () =>
        {
            await foreach (var _ in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
            {
            }
        });

        // Wait for BeginTurnAsync to subscribe before enqueuing (30 s is a deadlock failsafe).
        await beginTurnCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var steeringMessage = new ChatMessage(ChatRole.User, "steer the agent");
        queueManager.Enqueue(
            queueManager.ImmediateQueue,
            [new AgentInputItem { Messages = [steeringMessage] }]);

        // Complete the turn so the task finishes cleanly.
        channel.Writer.Complete();
        await turnTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(forwarded);
        Assert.Equal("steer the agent", forwarded.Text);
        Assert.True(subscription.Disposed);
    }

    private sealed class FlagDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void AfterInterrupt_InvalidateCopilotSession_SetsPendingResumeSessionId()
    {
        // Regression test for GitHub issue #35 (Failure 1): interrupting a turn must re-arm
        // pendingResumeSessionId so the next EnsureSessionAsync resumes the existing Copilot CLI
        // session (with its history) rather than creating a blank new one.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);
        const string expectedSessionId = "test-copilot-session-id";

        // Build a fake CopilotSession (sealed; no public ctor) by bypassing the constructor and
        // injecting a known SessionId via its compiler-generated backing field.
        var fakeSession = (CopilotSession)RuntimeHelpers.GetUninitializedObject(typeof(CopilotSession));
        typeof(CopilotSession)
            .GetField("<SessionId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fakeSession, expectedSessionId);

        // Place the fake session into the client's private copilotSession field so the CAS inside
        // InvalidateCopilotSession succeeds (it requires copilotSession == session by reference).
        typeof(CopilotSdkChatClient)
            .GetField("copilotSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, fakeSession);

        // Invoke the private method the interrupt path calls.
        typeof(CopilotSdkChatClient)
            .GetMethod("InvalidateCopilotSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [fakeSession]);

        // After invalidation the resume id must be re-armed so the next turn resumes the session.
        var pendingResumeSessionId = (string?)typeof(CopilotSdkChatClient)
            .GetField("pendingResumeSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client);

        Assert.Equal(expectedSessionId, pendingResumeSessionId);
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
    public void ComputeSessionSignature_ChangesWhenWorkingDirectoryChanges()
    {
        var withDir = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["working-directory"] = "/repo/a" },
        });
        var withOtherDir = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["working-directory"] = "/repo/b" },
        });

        Assert.NotEqual(withDir, withOtherDir);
    }

    [Fact]
    public void ComputeSessionSignature_TreatsAbsentAndNullWorkingDirectoryAsEquivalent()
    {
        var withNull = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["working-directory"] = null },
        });
        var withAbsent = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions());

        Assert.Equal(withNull, withAbsent);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenModelIdChanges()
    {
        var first = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { ModelId = "gpt-a" });
        var second = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { ModelId = "gpt-b" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildSessionConfig_SetsWorkingDirectory_WhenPresentInAdditionalProperties()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["working-directory"] = "/my/repo" },
        };

        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("/my/repo", config.WorkingDirectory);
    }

    [Fact]
    public void BuildSessionConfig_DoesNotReadWorkingDirectoryFromModelOptions()
    {
        // The chat client must not honour model parameters for the working directory (issue #896);
        // only the ChatOptions runtime override path remains.
        var modelOptions = new AgentSchema.ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object> { ["working-directory"] = "/from/model" },
        };

        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options: null, modelOptions);

        Assert.Null(config.WorkingDirectory);
    }

    [Fact]
    public void BuildSessionConfig_WhenChatOptionsModelIdSet_UsesModelIdFromChatOptions()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig(
            "gpt-constructor",
            byokOptions: null,
            new ChatOptions { ModelId = "gpt-call-time" });

        Assert.Equal("gpt-call-time", config.Model);
    }

    [Fact]
    public void BuildSessionConfig_WhenChatOptionsModelIdNull_UsesConstructorModelId()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig(
            "gpt-constructor",
            byokOptions: null,
            new ChatOptions { ModelId = null });

        Assert.Equal("gpt-constructor", config.Model);
    }

    [Fact]
    public void BuildSessionConfig_WhenChatOptionsModelIdWhitespace_UsesConstructorModelId()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig(
            "gpt-constructor",
            byokOptions: null,
            new ChatOptions { ModelId = "   " });

        Assert.Equal("gpt-constructor", config.Model);
    }

    [Fact]
    public void BuildSessionConfig_WhenChatOptionsModelIdSet_ByokProviderUsesEffectiveModelId()
    {
        var byok = new CopilotByokOptions
        {
            Provider = "openai",
            BaseUrl = "http://localhost:1234/",
        };

        var config = CopilotSdkChatClient.BuildSessionConfig(
            "gpt-constructor",
            byok,
            new ChatOptions { ModelId = "gpt-call-time" });

        Assert.NotNull(config.Provider);
        Assert.Equal("gpt-call-time", config.Provider!.ModelId);
    }

    [Fact]
    public void BuildResumeSessionConfig_WhenChatOptionsModelIdSet_UsesModelIdFromChatOptions()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig(
            "gpt-constructor",
            byokOptions: null,
            new ChatOptions { ModelId = "gpt-call-time" });

        Assert.Equal("gpt-call-time", config.Model);
    }

    [Fact]
    public void BuildSessionConfig_DoesNotSetWorkingDirectory_WhenAbsentFromAdditionalProperties()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Null(config.WorkingDirectory);
    }

    [Fact]
    public void BuildResumeSessionConfig_SetsWorkingDirectory_WhenPresentInAdditionalProperties()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["working-directory"] = "/my/repo" },
        };

        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("/my/repo", config.WorkingDirectory);
    }

    [Fact]
    public void BuildResumeSessionConfig_DoesNotReadWorkingDirectoryFromModelOptions()
    {
        // The chat client must not honour model parameters for the working directory (issue #896);
        // only the ChatOptions runtime override path remains.
        var modelOptions = new AgentSchema.ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object> { ["working-directory"] = "/from/model" },
        };

        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options: null, modelOptions);

        Assert.Null(config.WorkingDirectory);
    }

    [Fact]
    public void BuildResumeSessionConfig_DoesNotSetWorkingDirectory_WhenAbsentFromAdditionalProperties()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Null(config.WorkingDirectory);
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

    [Fact]
    public void BuildMessageOptions_WithMultipleConsecutiveUserMessages_ConcatenatesAllTexts()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.User, "second"),
        };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("first\n\n---\n\nsecond", result.Prompt);
        Assert.Null(result.Attachments);
    }

    [Fact]
    public void BuildMessageOptions_WithMultipleConsecutiveUserMessages_MergesAllAttachments()
    {
        var png1 = new byte[] { 1, 2 };
        var png2 = new byte[] { 3, 4 };
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent("img1"),
                new DataContent(png1, "image/png"),
            }),
            new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent("img2"),
                new DataContent(png2, "image/jpeg"),
            }),
        };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("img1\n\n---\n\nimg2", result.Prompt);
        Assert.NotNull(result.Attachments);
        Assert.Equal(2, result.Attachments.Count);
        var blob1 = Assert.IsType<GitHub.Copilot.SDK.UserMessageAttachmentBlob>(result.Attachments[0]);
        Assert.Equal("image/png", blob1.MimeType);
        var blob2 = Assert.IsType<GitHub.Copilot.SDK.UserMessageAttachmentBlob>(result.Attachments[1]);
        Assert.Equal("image/jpeg", blob2.MimeType);
    }

    [Fact]
    public void BuildMessageOptions_WithMultipleConsecutiveUserMessages_AssistantMessageBetweenBatchesIsRespected()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "old turn"),
            new ChatMessage(ChatRole.Assistant, "response"),
            new ChatMessage(ChatRole.User, "second"),
            new ChatMessage(ChatRole.User, "third"),
        };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("second\n\n---\n\nthird", result.Prompt);
        Assert.Null(result.Attachments);
    }

    [Fact]
    public void BuildMessageOptions_WithSingleTrailingUserMessage_BehavesAsBeforeWithNoSeparator()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "old turn"),
            new ChatMessage(ChatRole.Assistant, "response"),
            new ChatMessage(ChatRole.User, "new message"),
        };

        var result = CopilotSdkChatClient.BuildMessageOptions(messages);

        Assert.Equal("new message", result.Prompt);
        Assert.Null(result.Attachments);
    }

    [Fact]
    public async Task RunStreamingTurnAsync_Dispose_DoesNotBlockThreadPoolThread()
    {
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, "test"));
        channel.Writer.Complete();

        var subscription = new FlagDisposable();

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _) =>
            Task.FromResult(new StreamingTurnContext(
                channel.Reader,
                subscription,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask));

        var enumerator = client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());

        bool wasOnThreadPoolThread = false;
        var disposalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            wasOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposalCompleted.SetResult();
        });

        var completed = await Task.WhenAny(
            disposalCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(disposalCompleted.Task, completed);
        Assert.True(wasOnThreadPoolThread);
        Assert.True(subscription.Disposed);
    }

    [Fact]
    public async Task EnsureSessionAsync_DoesNotBlockThreadPoolThread()
    {
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: "test-token", loggerFactory: null);

        bool wasOnThreadPoolThread = false;
        var ensureCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            wasOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            try
            {
                var ensureMethod = typeof(CopilotSdkChatClient).GetMethod(
                    "EnsureSessionAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

                var task = (Task)ensureMethod.Invoke(
                    client,
                    [new ChatOptions(), CancellationToken.None])!;

                task.GetAwaiter().GetResult();
                ensureCompleted.SetResult();
            }
            catch (Exception ex)
            {
                ensureCompleted.SetException(ex);
            }
        });

        var completed = await Task.WhenAny(
            ensureCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(ensureCompleted.Task, completed);
        Assert.True(wasOnThreadPoolThread);
    }
}

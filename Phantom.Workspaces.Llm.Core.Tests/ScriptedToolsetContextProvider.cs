using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// Test double <see cref="AIContextProvider"/> for exercising AgentChat tool-initialization
/// behaviour: it can gate its completion on a caller-controlled release task, throw to simulate a
/// failing toolset load, and record the managed-thread id it executes on so tests can assert that
/// running-item mutations during initialization occur on the foreground scheduler (issues #1068 /
/// #1072).
/// </summary>
internal sealed class ScriptedToolsetContextProvider : AIContextProvider
{
    private readonly string stateKey = $"scripted-toolset:{Guid.NewGuid():n}";
    private readonly AITool[] tools;
    private readonly TaskCompletionSource? invoked;
    private readonly Task? release;
    private readonly Exception? failure;
    private readonly Action? onInvoke;
    private int invocationCount;

    public ScriptedToolsetContextProvider(
        AITool[]? tools = null,
        TaskCompletionSource? invoked = null,
        Task? release = null,
        Exception? failure = null,
        Action? onInvoke = null)
        : base(null, null, null)
    {
        this.tools = tools ?? [];
        this.invoked = invoked;
        this.release = release;
        this.failure = failure;
        this.onInvoke = onInvoke;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;

        // Gating, failure injection and the invoked signal apply only to the first invocation,
        // which corresponds to the tool-load step during AgentChat initialization. Later
        // invocations (made while processing a turn) return the tools immediately so a queued
        // turn can complete while initialization is still gated.
        var isFirstInvocation = Interlocked.Increment(ref this.invocationCount) == 1;
        if (!isFirstInvocation)
        {
            return new AIContext { Tools = this.tools };
        }

        this.onInvoke?.Invoke();
        this.invoked?.TrySetResult();

        if (this.release is not null)
        {
            await this.release.WaitAsync(cancellationToken);
        }

        if (this.failure is not null)
        {
            throw this.failure;
        }

        return new AIContext { Tools = this.tools };
    }
}

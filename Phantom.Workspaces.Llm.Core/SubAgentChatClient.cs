using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class SubAgentChatClient : IChatClient, ISubAgentChat, IHostedAgentChatClient, IRunningSubAgent
{
    private readonly Channel<ChatResponseUpdate> channel =
        Channel.CreateUnbounded<ChatResponseUpdate>();

    private volatile AgentChatCompletionState completionState = AgentChatCompletionState.Running;
    private DateTime lastUpdatedAt = DateTime.UtcNow;

    public string AgentId { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public AgentChatCompletionState CompletionState => completionState;

    public DateTime LastUpdatedAt => lastUpdatedAt;

    public IReadOnlyList<IRunningSubAgent> SubAgents => [];

    public event EventHandler? CompletionStateChanged;

    public SubAgentChatClient(string agentId, string displayName, string description = "")
    {
        AgentId = agentId;
        DisplayName = displayName;
        Description = description;
    }

    public void Push(ChatResponseUpdate update)
    {
        channel.Writer.TryWrite(update);
    }

    public void Complete()
    {
        completionState = AgentChatCompletionState.Succeeded;
        lastUpdatedAt = DateTime.UtcNow;
        channel.Writer.TryComplete();
        CompletionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Fail(Exception ex)
    {
        completionState = AgentChatCompletionState.Failed;
        lastUpdatedAt = DateTime.UtcNow;
        channel.Writer.TryComplete(ex);
        CompletionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Hosted sub-agent chat clients do not accept direct calls.");

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose() { }
}

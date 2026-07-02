using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly List<SubAgentActivityLine> recentActivity = [];
    private volatile AgentChatCompletionState completionState = AgentChatCompletionState.Running;

    public string AgentId { get; }
    public string DisplayName { get; }

    public AgentChatCompletionState CompletionState => completionState;

    public IReadOnlyList<SubAgentActivityLine> RecentActivity => recentActivity;

    public IReadOnlyList<IRunningSubAgent> SubAgents => [];

    public event EventHandler? ActivityChanged;

    public SubAgentChatClient(string agentId, string displayName)
    {
        AgentId = agentId;
        DisplayName = displayName;
    }

    public void Push(ChatResponseUpdate update)
    {
        channel.Writer.TryWrite(update);
        UpdateRecentActivity(update);
    }

    public void Complete()
    {
        completionState = AgentChatCompletionState.Succeeded;
        channel.Writer.TryComplete();
    }

    public void Fail(Exception ex)
    {
        completionState = AgentChatCompletionState.Failed;
        channel.Writer.TryComplete(ex);
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

    private void UpdateRecentActivity(ChatResponseUpdate update)
    {
        SubAgentActivityLine? line = null;

        if (update.Contents.OfType<FunctionCallContent>().Any())
        {
            var call = update.Contents.OfType<FunctionCallContent>().First();
            line = new SubAgentActivityLine(SubAgentActivityKind.ToolCall, call.Name ?? string.Empty);
        }
        else if (!string.IsNullOrEmpty(update.Text))
        {
            line = new SubAgentActivityLine(SubAgentActivityKind.AgentText, update.Text);
        }

        if (line is null)
            return;

        if (recentActivity.Count == 5)
            recentActivity.RemoveAt(0);

        recentActivity.Add(line);
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }
}

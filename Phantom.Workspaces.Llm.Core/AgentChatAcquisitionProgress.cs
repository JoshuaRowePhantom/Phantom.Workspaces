namespace Phantom.Workspaces.Llm;

/// <summary>Attempt-scoped observation, never ownership, of a chat under construction.</summary>
public sealed class AgentChatAcquisitionProgress : IDisposable
{
    private static readonly AsyncLocal<AgentChatAcquisitionProgress?> current = new();
    private readonly AgentChatAcquisitionProgress? previous;
    private readonly string sessionId;
    private readonly Action<IAgentChat> observe;
    private readonly Action? cancel;
    private readonly CancellationToken cancellation;
    private AgentChat? chat;

    public AgentChatAcquisitionProgress(string sessionId, Action<IAgentChat> observe,
        Action? cancel = null, CancellationToken cancellation = default)
    {
        this.sessionId = sessionId;
        this.observe = observe;
        this.cancel = cancel;
        this.cancellation = cancellation;
        this.previous = current.Value;
        current.Value = this;
    }

    internal static void OnConstructed(AgentChat chat, string? requestedSessionId)
    {
        if (current.Value is { } scope && scope.sessionId == requestedSessionId)
        {
            scope.chat = chat;
            scope.observe(chat);
        }
    }

    public static async Task<IAsyncDisposable?> WaitingForAuthorizationAsync(
        string safeName, Func<CancellationToken, Task> resume)
    {
        if (current.Value is not { chat: { } chat } scope || scope.cancellation.IsCancellationRequested)
            return null;
        var signIn = AgentStartupActions.Register(chat, async () =>
        {
            if (!scope.cancellation.IsCancellationRequested)
                await resume(scope.cancellation).ConfigureAwait(false);
        });
        var cancel = AgentStartupActions.Register(chat, () =>
        {
            if (!scope.cancellation.IsCancellationRequested)
                scope.cancel?.Invoke();
            return Task.CompletedTask;
        });
        AgentChatRunningItem? item = null;
        await chat.RunOnForegroundAsync(() =>
        {
            item = chat.CreateRunningItem(new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = [new Microsoft.Extensions.AI.TextContent(
                    $"Waiting for your authorization: {System.Net.WebUtility.HtmlEncode(safeName)}\n\n[Open sign-in]({signIn}) · [Cancel startup]({cancel})")],
            });
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return new AuthorizationProgress(chat, item!, signIn, cancel);
    }

    private sealed class AuthorizationProgress(AgentChat chat, AgentChatRunningItem item, string signIn, string cancel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            AgentStartupActions.Remove(chat, signIn);
            AgentStartupActions.Remove(chat, cancel);
            await chat.RunOnForegroundAsync(() =>
            {
                chat.CompleteRunningItem(item, writeToHistory: false);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    public void Dispose() => current.Value = this.previous;
}

using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotSession : IAsyncDisposable
{
    string SessionId { get; }
    IDisposable Subscribe(Action<SessionEvent> handler);
    Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken);
    Task SendAsync(MessageOptions options, CancellationToken cancellationToken);
    Task AbortAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Changes the active model on the live session without tearing it down, so the conversation
    /// history is retained (issue #1418). Delegates to the SDK's
    /// <see cref="CopilotSession.SetModelAsync(string, CancellationToken)"/>.
    /// </summary>
    Task SetModelAsync(string modelId, CancellationToken cancellationToken);
}

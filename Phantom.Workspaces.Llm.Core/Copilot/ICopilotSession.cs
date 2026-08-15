using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotSession : IAsyncDisposable
{
    string SessionId { get; }
    IDisposable Subscribe(Action<SessionEvent> handler);
    Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken);
    Task SendAsync(MessageOptions options, CancellationToken cancellationToken);
    Task AbortAsync(CancellationToken cancellationToken);
}

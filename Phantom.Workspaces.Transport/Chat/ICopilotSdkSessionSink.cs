namespace Phantom.Workspaces.Transport.Chat;

/// <summary>
/// A capability exposed by a chat client that wraps a GitHub Copilot SDK session, allowing
/// callers (notably <c>AgentChat</c>) to observe SDK session establishment and to arm a resume
/// session id ahead of the next turn. The concrete implementations are
/// <c>CopilotSdkChatClient</c> (local) and the proxy returned by
/// <see cref="ChatClientOverTransport"/> (remote), which forwards the same operations across the
/// wire so remote-hosted Copilot SDK sessions can be persisted and resumed by the source.
/// </summary>
public interface ICopilotSdkSessionSink
{
    /// <summary>
    /// Raised after a Copilot SDK session has been created or resumed, carrying its stable
    /// session id so the owning <c>AgentChat</c> can persist it for later resumption.
    /// </summary>
    event Action<string>? SessionEstablished;

    /// <summary>
    /// Arms the underlying Copilot SDK client so that the next session it needs will resume the
    /// supplied id rather than create a fresh one. A null/empty value clears any pending id.
    /// </summary>
    void SetResumeSessionId(string? sessionId);
}

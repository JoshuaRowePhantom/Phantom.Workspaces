using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Push interface for streaming <see cref="ChatResponseUpdate"/> events into a sub-agent chat
/// that is driven by an external source (e.g. <c>CopilotSdkChatClient</c>).
/// Resolved via <c>IChatClient.GetService&lt;ICopilotSubAgentReceiver&gt;()</c>.
/// </summary>
public interface ICopilotSubAgentReceiver
{
    /// <summary>Enqueues an update to be yielded by the streaming consumer.</summary>
    void Push(ChatResponseUpdate update);

    /// <summary>Signals that all updates have been pushed; closes the stream normally.</summary>
    void Complete();

    /// <summary>Faults the stream with the given exception.</summary>
    void Fail(Exception exception);
}

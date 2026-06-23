using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Marker for <see cref="IChatClient"/> implementations that invoke their own tools (for example the
/// GitHub Copilot SDK, where the <c>copilot</c> CLI drives the agentic loop and executes tools itself).
///
/// Such clients must be used by the agent framework <em>as-is</em>, without the framework adding its
/// function-invoking middleware: that middleware intercepts and buffers streaming
/// <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/> updates, which prevents the
/// client's live tool-call / tool-result events from streaming into the GUI as they happen (they would
/// otherwise only appear once the turn completes). It is also unnecessary, since the client invokes the
/// tools itself.
/// </summary>
public interface ISelfInvokingToolChatClient
{
}

using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

/// <summary>
/// Optional capability an <see cref="IChatClient"/> may expose (via
/// <see cref="IChatClient.GetService(Type, object?)"/>) to accept steering messages injected into
/// an in-progress streaming turn. When a <c>steering</c> frame arrives,
/// <see cref="ChatClientTransportSession"/> resolves this capability from the session's chat client
/// and forwards the deserialized <see cref="ChatMessage"/>, mirroring how the Copilot SDK client
/// forwards immediate steering input to a live session. Chat clients that do not implement this
/// interface simply ignore steering frames.
/// </summary>
public interface IChatSteeringTarget
{
    /// <summary>
    /// Injects a steering <paramref name="message"/> into the in-progress streaming turn.
    /// </summary>
    void InjectSteeringMessage(ChatMessage message);
}

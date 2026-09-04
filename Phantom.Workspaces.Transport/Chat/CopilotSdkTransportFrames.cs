using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Transport.Chat;

/// <summary>
/// Frame constants for the transport-level Copilot SDK session bridge that lets a remote
/// <c>CopilotSdkChatClient</c> raise its <c>SessionEstablished</c> event on, and be armed with a
/// resume-session id from, a <see cref="ChatClientOverTransport"/> on the source (issue #1319).
/// </summary>
internal static class CopilotSdkTransportFrames
{
    public const string SessionEstablishedType = "copilot-sdk-session-established";
    public const string SetResumeSessionIdType = "copilot-sdk-set-resume-session-id";
    public const string SessionIdProperty = "session-id";

    // Optional property on the initial chat-client request JsonObject; when present the remote
    // ChatClientTransportListener applies it to the freshly built client's ICopilotSdkSessionSink
    // synchronously before starting the frame pump, so the first turn's session is resumed
    // atomically without racing against the process-streaming frame.
    public const string ResumeSessionIdInitialProperty = "copilot-sdk-resume-session-id";

    public static JsonElement BuildSessionEstablished(string sessionId)
    {
        var obj = new JsonObject
        {
            ["type"] = SessionEstablishedType,
            [SessionIdProperty] = sessionId,
        };
        return JsonSerializer.SerializeToElement(obj);
    }

    public static JsonElement BuildSetResumeSessionId(string? sessionId)
    {
        var obj = new JsonObject
        {
            ["type"] = SetResumeSessionIdType,
            [SessionIdProperty] = sessionId,
        };
        return JsonSerializer.SerializeToElement(obj);
    }
}

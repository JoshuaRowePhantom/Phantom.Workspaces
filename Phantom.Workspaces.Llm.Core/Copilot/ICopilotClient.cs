using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotClient : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
    Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);
    Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken);
    Task<EventsReadResult> ReadPersistedEventsAsync(ReadCopilotPersistedEventsRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client does not expose persisted event replay.");
}

internal sealed record ReadCopilotPersistedEventsRequest(
    string SessionId,
    string? Cursor = null,
    long? Max = null,
    EventsReadDirection? Direction = null);

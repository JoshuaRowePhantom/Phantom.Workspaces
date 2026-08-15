using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotClient : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
    Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);
    Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken);
}

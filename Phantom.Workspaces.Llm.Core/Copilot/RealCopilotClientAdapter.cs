using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal sealed class RealCopilotClientAdapter : ICopilotClient
{
    private readonly CopilotClient inner;

    public RealCopilotClientAdapter(CopilotClient inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        this.inner.StartAsync(cancellationToken);

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = await this.inner.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models as IReadOnlyList<ModelInfo> ?? models.ToList();
    }

    public async Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        var session = await this.inner.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        return new RealCopilotSessionAdapter(session);
    }

    public async Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
    {
        var session = await this.inner.ResumeSessionAsync(sessionId, config, cancellationToken).ConfigureAwait(false);
        return new RealCopilotSessionAdapter(session);
    }

    public ValueTask DisposeAsync() => this.inner.DisposeAsync();
}

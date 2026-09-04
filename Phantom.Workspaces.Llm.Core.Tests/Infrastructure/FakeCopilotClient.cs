using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests.Infrastructure;

internal sealed class FakeCopilotClient : ICopilotClient
{
    private readonly FakeCopilotSession session;
    private bool started;

    public FakeCopilotClient(FakeCopilotSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// When set, <see cref="StartAsync"/> throws this exception instead of starting. Used to
    /// simulate the SDK's "Copilot runtime not found" failure (issue #1376).
    /// </summary>
    public Exception? StartException { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (this.StartException is not null)
        {
            throw this.StartException;
        }

        this.started = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (!this.started)
        {
            throw new InvalidOperationException("Client not started.");
        }

        return Task.FromResult<IReadOnlyList<ModelInfo>>(this.session.Models);
    }

    public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        if (!this.started)
        {
            throw new InvalidOperationException("Client not started.");
        }

        this.session.OnCreateSession(config);
        return Task.FromResult<ICopilotSession>(this.session);
    }

    public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
    {
        if (!this.started)
        {
            throw new InvalidOperationException("Client not started.");
        }

        this.session.OnResumeSession(sessionId, config);
        return Task.FromResult<ICopilotSession>(this.session);
    }

    public ValueTask DisposeAsync()
    {
        this.started = false;
        return ValueTask.CompletedTask;
    }
}

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001
using Phantom.Workspaces.Llm.Copilot;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

internal sealed class FakeCopilotClient : ICopilotClient
{
    public Task<EventsReadResult> ReadPersistedEventsAsync(ReadCopilotPersistedEventsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new EventsReadResult { Events = [], HasMore = false });
    private readonly FakeCopilotSession session;
    private bool started;

    public FakeCopilotClient(FakeCopilotSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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
        // Intentionally do NOT flip 'started' back to false. The fake is shared across successive
        // per-channel CopilotSdkChatClient instances in RemoteCopilotSdkSessionTests; the outgoing
        // client's fire-and-forget DisposeCoreAsync can race with the incoming client's StartAsync
        // and would otherwise turn every subsequent CreateSession/ResumeSession call into a
        // "Client not started." throw.
        return ValueTask.CompletedTask;
    }
}

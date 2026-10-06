using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001

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

    public Task<EventsReadResult> ReadPersistedEventsAsync(ReadCopilotPersistedEventsRequest request, CancellationToken cancellationToken) =>
        this.inner.Rpc.Sessions.ReadPersistedEventsAsync(
            sessionId: request.SessionId,
            cursor: request.Cursor,
            max: request.Max,
            direction: request.Direction,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<AgentRegistryLiveTargetEntry>> ListLiveSessionStatesAsync(CancellationToken cancellationToken)
    {
        // SDK 1.0.13 exposes registry entries only in agentRegistry.spawn's response, not in a
        // list/watch RPC. sessions.list supplies the owning host's live status for existing sessions.
        var sessions = await this.inner.Rpc.Sessions.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return sessions.Sessions.Where(entry => entry.HostStatus is not null)
            .Select(entry => new AgentRegistryLiveTargetEntry
            {
                SessionId = entry.SessionId,
                Status = entry.HostStatus == RemoteSessionHostStatus.Working
                    ? AgentRegistryLiveTargetEntryStatus.Working
                    : entry.HostStatus == RemoteSessionHostStatus.InputNeeded
                        ? AgentRegistryLiveTargetEntryStatus.Waiting
                        : entry.HostStatus == RemoteSessionHostStatus.Error
                            ? AgentRegistryLiveTargetEntryStatus.Attention
                            : AgentRegistryLiveTargetEntryStatus.Done,
            }).ToArray();
    }

    public ValueTask DisposeAsync() => this.inner.DisposeAsync();
}

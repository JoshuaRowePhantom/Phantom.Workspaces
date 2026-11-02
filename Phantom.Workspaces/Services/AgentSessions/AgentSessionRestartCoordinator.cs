using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed class AgentSessionRestartCoordinator : IAgentSessionRestartService
{
    internal static AgentSessionRestartCoordinator Instance { get; } = new();

    public AgentSessionRestartAvailability GetAvailability(
        AgentSessionWorkspaceTabViewModel tab,
        TabCommandContext context)
        => new()
        {
            IsAvailable = GetRetryIdentity(tab) is not null
                || tab.Lease?.RestartCommandState.Result?.State != AgentSessionOwnerRestartState.Blocked
                    && GetIdentity(tab) is not null && tab.Lease?.RestartRuntimeAsync is not null,
            DisabledReason = tab.Lease?.RestartCommandState.Result?.Message
                ?? (tab.Lease?.AgentChat is RemoteAgentChat remote
                ? remote.RestartClient.RestartDisabledReason ?? "This remote runtime has no durable restart authority."
                : "This session has no independently restartable owning runtime; parent-managed children share their parent's lifecycle."),
        };

    public AgentSessionRestartRequest? PrepareRestart(
        AgentSessionWorkspaceTabViewModel tab,
        TabCommandContext context)
        => tab.Lease?.RestartCommandState.Result?.State != AgentSessionOwnerRestartState.Blocked
            && (GetRetryIdentity(tab) ?? GetIdentity(tab)) is { } identity
            && tab.Lease is { RestartRuntimeAsync: not null } lease
            ? new AgentSessionRestartRequest
            {
                SessionId = identity.AgentSessionId,
                ExpectedChat = lease.AgentChat,
                ExpectedLease = lease,
                ExpectedOwnerId = identity.OwningProfileEntityId,
                OwnershipGeneration = identity.OwnershipGeneration,
                RuntimeEpoch = identity.RuntimeEpoch.Value,
                CommandId = GetRetryIdentity(tab) is not null
                    ? lease.RestartCommandState.Pending!.CommandId : Guid.NewGuid(),
            }
            : null;

    public async Task<AgentSessionRestartResult> RestartAsync(
        AgentSessionRestartRequest request,
        CancellationToken cancellationToken)
    {
        var lease = request.ExpectedLease;
        if (lease?.RestartRuntimeAsync is not { } restart)
            return new AgentSessionRestartResult { Outcome = AgentSessionRestartOutcome.TargetChanged };
        var ownerRequest = OwnerRequest(request);
        lease.RestartCommandState.Pending = ownerRequest;
        var result = await restart(ownerRequest, cancellationToken).ConfigureAwait(false);
        lease.RestartCommandState.Result = result;
        if (result.State is AgentSessionOwnerRestartState.Succeeded or AgentSessionOwnerRestartState.TargetChanged)
            lease.RestartCommandState.Pending = null;
        return Result(result);
    }

    public async Task<AgentSessionRestartResult?> GetStatusAsync(
        AgentSessionRestartRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedLease?.RestartStatusAsync is not { } status)
            return null;
        var result = await status(OwnerRequest(request), cancellationToken).ConfigureAwait(false);
        return result is null ? null : Result(result);
    }

    private static AgentSessionOwnerRestartRequest OwnerRequest(AgentSessionRestartRequest request) => new()
    {
        CommandId = request.CommandId,
        ExpectedRuntime = new AgentSessionRuntimeIdentity
        {
            AgentSessionId = request.SessionId,
            OwningProfileEntityId = request.ExpectedOwnerId,
            OwnershipGeneration = request.OwnershipGeneration,
            RuntimeEpoch = new RuntimeEpoch { Value = request.RuntimeEpoch },
        },
    };

    private static AgentSessionRestartResult Result(AgentSessionOwnerRestartResult result)
        => new()
        {
            Outcome = result.State switch
            {
                AgentSessionOwnerRestartState.Succeeded => AgentSessionRestartOutcome.Succeeded,
                AgentSessionOwnerRestartState.TargetChanged => AgentSessionRestartOutcome.TargetChanged,
                _ => AgentSessionRestartOutcome.Failed,
            },
            Message = result.Message,
            CommandId = result.CommandId,
            ReplacementRuntime = result.ReplacementRuntime,
            CanRetry = result.State == AgentSessionOwnerRestartState.Stopped,
        };

    private static AgentSessionRuntimeIdentity? GetIdentity(AgentSessionWorkspaceTabViewModel tab)
        => tab.Lease?.AgentChat is RemoteAgentChat remote
            ? remote.RestartClient.RestartIdentity
            : tab.Lease?.RestartIdentityProvider is { } provider ? provider() : tab.Lease?.RestartIdentity;

    private static AgentSessionRuntimeIdentity? GetRetryIdentity(AgentSessionWorkspaceTabViewModel tab)
        => tab.Lease is { RestartRuntimeAsync: not null } lease
            && lease.RestartCommandState.Result?.State == AgentSessionOwnerRestartState.Stopped
                ? lease.RestartCommandState.Pending?.ExpectedRuntime : null;
}

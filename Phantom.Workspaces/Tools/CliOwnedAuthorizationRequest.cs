using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tools;

public enum VsCodeTunnelProvider { GitHub, Microsoft }

public enum CliOwnedAuthorizationKind { VsCodeTunnelGitHub, VsCodeTunnelMicrosoft }

public sealed record CliOwnedAuthorizationRequest
    : CompletionAuthorizationRequest<AuthorizationCompletedAccessResult>
{
    public required CliOwnedAuthorizationKind Kind { get; init; }
    public required string CliPath { get; init; }
    public required bool RequiresRenewal { get; init; }
}

public sealed class AuthorizationCompletedAccessResult : IAccessResult
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public interface IReleaseAuthorizationPolicy
{
    ValueTask<ReleasePolicyEvaluation> EvaluateAsync(
        ReleasePolicyRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ReleaseAuthorization> CommitAsync(
        ReleasePolicyDecision decision,
        CancellationToken cancellationToken = default);

    ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
        ReleaseAuthorizationIdentifier identifier,
        ReleasePolicyRequest request,
        CancellationToken cancellationToken = default);

    ValueTask FinalizeCredentialIdentityAsync(
        ReleaseAuthorizationIdentifier identifier,
        ResolvedCredentialIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask ForgetAsync(
        ReleaseMemoryIdentifier identifier,
        CancellationToken cancellationToken = default);
}

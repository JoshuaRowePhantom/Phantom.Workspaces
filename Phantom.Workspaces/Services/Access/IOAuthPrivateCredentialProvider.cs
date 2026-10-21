using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal interface IOAuthPrivateCredentialProvider
{
    ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(
        OAuthCredentialAccessRequest request,
        CancellationToken cancellationToken);

    ValueTask<ProviderAcquisitionOutcome> AcquirePrivateAsync(
        OAuthCredentialAccessRequest request,
        ICredentialExecutionContext parentContext,
        ProviderAcquisitionKey parentAcquisitionKey,
        ProviderInteractionPermit parentPermit,
        CancellationToken cancellationToken);

    ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
        OAuthCredentialAccessRequest request,
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken);

    ValueTask<OAuthGenerationUseOutcome<TResult>> UseGenerationAsync<TResult>(
        OAuthCredentialAccessRequest request,
        CredentialMaterialGenerationIdentifier generation,
        CredentialAccountIdentifier expectedAccount,
        ICredentialExecutionContext parentContext,
        ProviderAcquisitionKey parentAcquisitionKey,
        ProviderInteractionPermit parentPermit,
        Func<string, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken);
}

internal abstract record OAuthGenerationUseOutcome<TResult>;
internal sealed record OAuthGenerationUseSucceeded<TResult> : OAuthGenerationUseOutcome<TResult>
{
    public required TResult Result { get; init; }
}
internal sealed record OAuthGenerationUseUnavailable<TResult> : OAuthGenerationUseOutcome<TResult>;
internal sealed record OAuthGenerationUseDenied<TResult> : OAuthGenerationUseOutcome<TResult>;

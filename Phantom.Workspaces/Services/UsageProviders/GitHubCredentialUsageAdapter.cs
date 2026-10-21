using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.UsageProviders;

internal sealed class GitHubCredentialUsageAdapter(
    IAccessBroker? accessBroker,
    Func<AccessPresentationScope>? presentationScope = null)
{
    private readonly Func<AccessPresentationScope> getPresentationScope =
        presentationScope ?? (() => AccessPresentationScope.Runtime);

    public async ValueTask<TResult> UseAsync<TResult>(
        GitHubCredentialPurpose purpose,
        Func<string, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (accessBroker is null)
        {
            throw new InvalidOperationException("The access broker is required for GitHub usage.");
        }
        var request = GitHubCredentialAccessRequest.Create(
            purpose,
            new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, $"usage:{purpose}"),
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Usage provider",
            },
            new CredentialUseLineage(),
            $"refresh:{purpose}",
            this.getPresentationScope());
        var acquired = await accessBroker.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        if (acquired is not AccessSucceeded<GitHubCredentialAccessResult> succeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (acquired is AccessCancelled<GitHubCredentialAccessResult> or AccessShuttingDown<GitHubCredentialAccessResult>)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new InvalidOperationException("GitHub usage credential access was not authorized.");
        }
        await using var result = succeeded.Result;
        cancellationToken.ThrowIfCancellationRequested();
        var used = await result.Lease.UseAsync(async (material, token) =>
        {
            try
            {
                return await operation(material, token).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException exception)
            {
                throw new System.Net.Http.HttpRequestException("The GitHub usage HTTP operation failed.", null, exception.StatusCode);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (used is SensitiveValueUseSucceeded<TResult> completed) { return completed.Result; }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("GitHub usage credential authorization or reacquisition is required.");
    }
}

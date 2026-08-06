namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The global entry point for turning manifest-agnostic secret requests into lazy
/// <see cref="SecretRetriever"/> instances or per-secret failures.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Requests consent as needed and returns resolved retrievers/failures. Returns <see langword="null"/>
    /// only when the user refuses the whole request.
    /// </summary>
    Task<RequestSecretsResult?> RequestSecretsAsync(
        IReadOnlyList<SecretRequest> requests,
        CancellationToken cancellationToken);
}

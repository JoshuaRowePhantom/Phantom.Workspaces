namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The outcome of requesting a batch of secrets: the retrievers that were acquired and the
/// requests that failed.
/// </summary>
public sealed record RequestSecretsResult(
    IReadOnlyList<SecretRetriever> AcquiredSecrets,
    IReadOnlyList<SecretRequestFailure> FailedSecrets);

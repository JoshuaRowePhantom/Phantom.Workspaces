namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Thrown when one or more secrets could not be materialized. Carries the per-secret failures.
/// </summary>
public sealed class SecretMaterializationFailedException : Exception
{
    public SecretMaterializationFailedException(string message, IReadOnlyList<SecretRequestFailure> failures)
        : base(message)
    {
        Failures = failures;
    }

    public IReadOnlyList<SecretRequestFailure> Failures { get; }
}

using System.Security;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The single seam through which secret material is exposed, exclusively as a
/// <see cref="SecureString"/>. The underlying platform read happens lazily, only when the caller
/// awaits <see cref="Secret"/>.
/// </summary>
/// <remarks>
/// Callers must not stash the returned <see cref="SecureString"/> in a <see cref="string"/> field,
/// log it, print it, or persist it. The single approved conversion path to plaintext is
/// <see cref="SecureStringMarshal.Use{T}(SecureString, Func{string, T})"/>.
/// </remarks>
public sealed class SecretRetriever
{
    public required string SecretName { get; init; }

    public required Func<CancellationToken, Task<SecureString>> Secret { get; init; }
}

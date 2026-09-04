using System.Diagnostics.CodeAnalysis;
using System.Security;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The seam that lets consumers resolve a <c>${SECRET:&lt;useHandle&gt;}</c> reference token back
/// to a <see cref="SecureString"/> retriever. Populated by the secret materializer at
/// materialization time; handle tokens are opaque, per-materialization, never persisted, never
/// logged, never reused.
/// </summary>
public interface ISecretPlaceholderResolver
{
    /// <summary>
    /// True if <paramref name="placeholder"/> is a well-formed <c>${SECRET:&lt;useHandle&gt;}</c>
    /// token registered with this resolver. When true, <paramref name="retriever"/> is set to the
    /// <see cref="SecureString"/> accessor for that use. When false, callers must fall back to
    /// their existing <c>${VAR}</c> env-var expansion / literal handling path.
    /// </summary>
    bool TryResolve(string placeholder, [NotNullWhen(true)] out SecretRetriever? retriever);
}

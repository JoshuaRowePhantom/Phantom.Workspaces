using System.Diagnostics.CodeAnalysis;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The seam that lets consumers resolve a <c>${SECRET:&lt;useHandle&gt;}</c> reference token back
/// to a fixed-generation lease. Populated by the secret materializer at
/// materialization time; handle tokens are opaque, per-materialization, never persisted, never
/// logged, never reused.
/// </summary>
public interface ISecretPlaceholderResolver : IAsyncDisposable
{
    /// <summary>
    /// True if <paramref name="placeholder"/> is a well-formed <c>${SECRET:&lt;useHandle&gt;}</c>
    /// token registered with this resolver. The returned lease cannot reacquire or substitute material.
    /// </summary>
    bool TryResolve(string placeholder, [NotNullWhen(true)] out SensitiveValueLease? lease);
}

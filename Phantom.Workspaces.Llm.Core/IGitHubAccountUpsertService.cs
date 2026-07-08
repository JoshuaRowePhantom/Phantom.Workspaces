using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Ensures a <c>user-account</c> entity exists in the workspace data store for the GitHub
/// user identified by the supplied token.  The operation is idempotent: if an entity with the
/// same provider and username already exists it is left unchanged.
/// </summary>
public interface IGitHubAccountUpsertService
{
    /// <summary>
    /// Resolves the username for <paramref name="token"/> and upserts the corresponding
    /// <c>user-account</c> entity.  Failures are logged as warnings and never propagated to the
    /// caller so the primary auth flow is not affected.
    /// </summary>
    Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default);
}

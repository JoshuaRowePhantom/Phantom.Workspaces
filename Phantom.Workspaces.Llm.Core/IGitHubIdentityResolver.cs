using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves the GitHub username associated with a GitHub token by querying the GitHub REST API.
/// </summary>
public interface IGitHubIdentityResolver
{
    /// <summary>
    /// Returns the GitHub login name for the authenticated user that owns <paramref name="token"/>,
    /// or <see langword="null"/> when resolution fails (invalid token, no network, unexpected response).
    /// Implementations are expected to cache results so repeated calls for the same token do not
    /// incur additional HTTP round-trips.
    /// </summary>
    Task<string?> GetUsernameAsync(string token, CancellationToken cancellationToken = default);
}

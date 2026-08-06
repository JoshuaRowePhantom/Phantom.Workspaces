using System.Security;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The backend contract for reading, writing, deleting and enumerating secret <em>values</em> in a
/// per-user platform credential store. Values are exposed exclusively as <see cref="SecureString"/>;
/// the plaintext never crosses this seam.
/// </summary>
public interface IPlatformSecretStore
{
    /// <summary>
    /// Reads the secret stored under <paramref name="name"/>, or <see langword="null"/> when no such
    /// secret exists.
    /// </summary>
    Task<SecureString?> ReadAsync(string name, CancellationToken ct);

    /// <summary>Writes (creating or overwriting) the secret stored under <paramref name="name"/>.</summary>
    Task WriteAsync(string name, SecureString value, CancellationToken ct);

    /// <summary>Deletes the secret stored under <paramref name="name"/> if present.</summary>
    Task DeleteAsync(string name, CancellationToken ct);

    /// <summary>
    /// Enumerates the names of all stored secrets whose name begins with <paramref name="prefix"/>.
    /// </summary>
    Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct);
}

using System.Security;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The fallback <see cref="IPlatformSecretStore"/> used on platforms without a concrete credential
/// backend (macOS, Linux). Reads return <see langword="null"/> and enumeration returns an empty
/// list, while writes and deletes throw <see cref="PlatformNotSupportedException"/> so that any
/// attempt to persist a secret on an unsupported platform fails loudly rather than silently.
/// </summary>
public sealed class NullPlatformSecretStore : IPlatformSecretStore
{
    /// <inheritdoc />
    public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        => Task.FromResult<SecureString?>(null);

    /// <inheritdoc />
    public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        => throw new PlatformNotSupportedException(
            "Writing platform secrets is not supported on this platform.");

    /// <inheritdoc />
    public Task DeleteAsync(string name, CancellationToken ct)
        => throw new PlatformNotSupportedException(
            "Deleting platform secrets is not supported on this platform.");

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([]);
}

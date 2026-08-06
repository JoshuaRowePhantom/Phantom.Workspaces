using System.Runtime.Versioning;
using System.Security;
using Meziantou.Framework.Win32;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A thin adapter over the Windows Credential Manager (via
/// <see cref="Meziantou.Framework.Win32.CredentialManager"/>) that stores each secret under a
/// target name of the form <c>"{prefix}{name}"</c>. Values are marshalled to plaintext only for the
/// microseconds required to hand them to the credential API, via
/// <see cref="SecureStringMarshal.Use{T}(SecureString, System.Func{string, T})"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStore : IPlatformSecretStore
{
    /// <summary>The default target-name prefix for all Phantom.Workspaces credentials.</summary>
    public const string DefaultTargetPrefix = "Phantom.Workspaces:";

    private readonly string targetPrefix;

    /// <summary>
    /// Creates a store that reads and writes credentials under <paramref name="targetPrefix"/>.
    /// Defaults to <see cref="DefaultTargetPrefix"/>. Tests supply an isolated prefix so they never
    /// touch a user's real credentials.
    /// </summary>
    public WindowsCredentialManagerSecretStore(string? targetPrefix = null)
    {
        this.targetPrefix = targetPrefix ?? DefaultTargetPrefix;
    }

    /// <inheritdoc />
    public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();

        // Meziantou's CredentialManager is annotated [SupportedOSPlatform("windows5.1.2600")]; this
        // type is windows-only and every supported Windows release exceeds that baseline.
#pragma warning disable CA1416
        var credential = CredentialManager.ReadCredential(this.targetPrefix + name);
#pragma warning restore CA1416
        if (credential?.Password is not { } password)
        {
            return Task.FromResult<SecureString?>(null);
        }

        return Task.FromResult<SecureString?>(ToSecureString(password));
    }

    /// <inheritdoc />
    public Task WriteAsync(string name, SecureString value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        SecureStringMarshal.Use(value, plaintext =>
        {
#pragma warning disable CA1416
            CredentialManager.WriteCredential(
                applicationName: this.targetPrefix + name,
                userName: Environment.UserName,
                secret: plaintext,
                persistence: CredentialPersistence.LocalMachine);
#pragma warning restore CA1416
            return true;
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();

#pragma warning disable CA1416
        CredentialManager.DeleteCredential(this.targetPrefix + name);
#pragma warning restore CA1416
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ct.ThrowIfCancellationRequested();

        var filter = this.targetPrefix + prefix + "*";
#pragma warning disable CA1416
        var names = CredentialManager.EnumerateCredentials(filter)
#pragma warning restore CA1416
            .Select(credential => credential.ApplicationName)
            .Where(applicationName => applicationName.StartsWith(this.targetPrefix, StringComparison.Ordinal))
            .Select(applicationName => applicationName[this.targetPrefix.Length..])
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }
}

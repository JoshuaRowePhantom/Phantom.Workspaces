using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Builds the deterministic, UTF-8, pipe-delimited preimage strings that are SHA-256 hashed to
/// produce the stable identity of an allowed secret use, per <see cref="SecretUseScope"/>.
/// All preimages are prefixed with <see cref="VersionPrefix"/>.
/// </summary>
internal static class SecretUseScopePreimage
{
    public const string VersionPrefix = "phantom.workspaces/secret-store/v1";

    /// <summary>
    /// Builds the preimage for <paramref name="scope"/>. Returns an empty string for
    /// <see cref="SecretUseScope.AlwaysAsk"/>, which is never persisted and never matches.
    /// </summary>
    public static string Build(
        SecretUseScope scope,
        string secretName,
        string useDisplayString,
        string? stableManifestIdentity = null,
        string? manifestContentHash = null)
    {
        return scope switch
        {
            SecretUseScope.AllUses =>
                $"{VersionPrefix}|scope=all-uses",
            SecretUseScope.AnyManifest =>
                $"{VersionPrefix}|scope=any-manifest|secret={secretName}",
            SecretUseScope.KeyInAnyManifest =>
                $"{VersionPrefix}|scope=key-any-manifest|secret={secretName}|use={useDisplayString}",
            SecretUseScope.ManifestIdentity =>
                $"{VersionPrefix}|scope=manifest-identity|manifestId={stableManifestIdentity}|secret={secretName}",
            SecretUseScope.ManifestContent =>
                $"{VersionPrefix}|scope=manifest-content|manifestHash={manifestContentHash}|secret={secretName}",
            SecretUseScope.KeyInManifestContent =>
                $"{VersionPrefix}|scope=key-manifest-content|manifestHash={manifestContentHash}|secret={secretName}|use={useDisplayString}",
            SecretUseScope.AlwaysAsk => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown secret use scope."),
        };
    }

    /// <summary>
    /// Computes the lowercase-hex SHA-256 hash of the preimage for <paramref name="scope"/>.
    /// Returns an empty string for <see cref="SecretUseScope.AlwaysAsk"/> (which never matches).
    /// </summary>
    public static string ComputeHash(
        SecretUseScope scope,
        string secretName,
        string useDisplayString,
        string? stableManifestIdentity = null,
        string? manifestContentHash = null)
    {
        var preimage = Build(scope, secretName, useDisplayString, stableManifestIdentity, manifestContentHash);
        if (preimage.Length == 0)
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        return Convert.ToHexStringLower(bytes);
    }
}

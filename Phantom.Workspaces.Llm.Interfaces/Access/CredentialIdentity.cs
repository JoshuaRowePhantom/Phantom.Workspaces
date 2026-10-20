using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public abstract record CredentialDigest
{
    private string digest = string.Empty;

    private protected CredentialDigest() { }

    public required string Digest
    {
        get => this.digest;
        init
        {
            if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            {
                throw new ArgumentException("A SHA-256 digest is required.", nameof(value));
            }

            this.digest = value.ToLowerInvariant();
        }
    }
}

public sealed record LogicalCredentialIdentifier : CredentialDigest;
public sealed record CredentialAccountIdentifier : CredentialDigest;
public sealed record CredentialSourceIdentifier : CredentialDigest;
public sealed record AccessRequestorIdentifier : CredentialDigest;
public sealed record CredentialUseFingerprint : CredentialDigest;
public sealed record CredentialLineageIdentifier : CredentialDigest;

public sealed record CredentialMaterialGenerationIdentifier
{
    public required Guid Value { get; init; }
}

public enum CredentialProviderKind { StoredSecret, GitHub, Entra, OAuth, McpOAuth, DevTunnel, CliOwned }
public enum CredentialKind { Secret, AccessToken, IdentityToken, ConnectToken, OpaqueUse, Completion }
public enum AccessRequestorKind { Application, Agent, Tool, Manifest, McpServer }

public sealed record CredentialReference
{
    public required LogicalCredentialIdentifier LogicalIdentifier { get; init; }
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialKind Kind { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
}

public sealed record AccessRequestor
{
    public required AccessRequestorIdentifier Identifier { get; init; }
    public required AccessRequestorKind Kind { get; init; }
    public required string SafeDisplayName { get; init; }
}

public sealed record CredentialIdentityConfiguration
{
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialKind Kind { get; init; }
    public required string CredentialPartition { get; init; }
    public required string SourceCompatibility { get; init; }
    public required string Authority { get; init; }
    public required string Audience { get; init; }
    public required string ClientPartition { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
}

public static class CredentialIdentityFactory
{
    public static LogicalCredentialIdentifier CreateLogicalIdentifier(CredentialIdentityConfiguration configuration) => new()
    {
        Digest = Compute("logical", new[]
        {
            configuration.Provider.ToString(),
            configuration.Kind.ToString(),
            configuration.CredentialPartition,
            configuration.SourceCompatibility,
            configuration.Authority,
            configuration.Audience,
            configuration.ClientPartition,
        }.Concat(configuration.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))),
    };

    public static CredentialAccountIdentifier CreateAccountIdentifier(
        CredentialProviderKind provider,
        string stableAccountIdentifier) => new()
    {
        Digest = Compute("account", new[] { provider.ToString(), stableAccountIdentifier }),
    };

    public static CredentialSourceIdentifier CreateSourceIdentifier(
        CredentialProviderKind provider,
        string sourceConfiguration) => new()
    {
        Digest = Compute("source", new[] { provider.ToString(), sourceConfiguration }),
    };

    public static AccessRequestorIdentifier CreateRequestorIdentifier(
        AccessRequestorKind kind,
        string stableIdentifier) => new()
    {
        Digest = Compute("requestor", new[] { kind.ToString(), stableIdentifier }),
    };

    public static CredentialLineageIdentifier CreateLineageIdentifier(
        string lineageKind,
        string stableIdentifier) => new()
    {
        Digest = Compute("lineage", new[] { lineageKind, stableIdentifier }),
    };

    public static CredentialUseFingerprint CreateUseFingerprint(
        CredentialUseKind kind,
        CredentialUseLineage lineage,
        AccessRequestorIdentifier requestor,
        string purpose) => new()
    {
        Digest = Compute("use", new[]
        {
            kind.ToString(), requestor.Digest, purpose,
            lineage.Session?.Digest ?? string.Empty,
            lineage.Manifest?.Digest ?? string.Empty,
            lineage.ManifestContent?.Digest ?? string.Empty,
            lineage.Key?.Digest ?? string.Empty,
            lineage.ProviderConfiguration?.Digest ?? string.Empty,
        }),
    };

    private static string Compute(
        string domain,
        IEnumerable<string> components)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("phantom.access.v1");
            writer.Write(domain);
            foreach (var component in components)
            {
                writer.Write(component);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

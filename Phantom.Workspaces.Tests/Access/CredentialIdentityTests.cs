using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class CredentialIdentityTests
{
    private static CredentialIdentityConfiguration Configuration => new()
    {
        Provider = CredentialProviderKind.OAuth,
        Kind = CredentialKind.AccessToken,
        CredentialPartition = "cache",
        SourceCompatibility = "provider",
        Authority = "https://authority.example",
        Audience = "resource",
        ClientPartition = "client",
        Scopes = new[] { "write", "read", "read" },
    };

    [Fact]
    public void CredentialIdentity_TokenRotation_ProducesSameLogicalIdentifier()
    {
        var identifier = CredentialIdentityFactory.CreateLogicalIdentifier(Configuration);
        Assert.Equal(identifier, CredentialIdentityFactory.CreateLogicalIdentifier(Configuration with { Scopes = new[] { "read", "write" } }));
        Assert.Equal(64, identifier.Digest.Length);
        Assert.DoesNotContain(typeof(CredentialIdentityConfiguration).GetProperties(),
            property => property.Name.Contains("Token") || property.Name.Contains("Expiry") || property.Name.Contains("Generation"));
    }

    [Fact]
    public void CredentialIdentity_SourceOrAudienceChange_ProducesDifferentIdentifier()
    {
        var identifier = CredentialIdentityFactory.CreateLogicalIdentifier(Configuration);
        Assert.NotEqual(identifier, CredentialIdentityFactory.CreateLogicalIdentifier(Configuration with { SourceCompatibility = "other" }));
        Assert.NotEqual(identifier, CredentialIdentityFactory.CreateLogicalIdentifier(Configuration with { Audience = "other" }));
        Assert.NotEqual(identifier, CredentialIdentityFactory.CreateLogicalIdentifier(Configuration with { Scopes = new[] { "read" } }));
        Assert.Throws<ArgumentException>(() => new CredentialAccountIdentifier { Digest = "unsafe-account-name" });
    }

    [Fact]
    public void CredentialUseDescriptor_DisplayText_DoesNotAffectFingerprint()
    {
        var lineage = new CredentialUseLineage { Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "manifest-one") };
        var requestor = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Agent, "agent");
        var descriptor = new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Manifest, lineage, requestor, "purpose"),
            Kind = CredentialUseKind.Manifest,
            Lineage = lineage,
            SafeDisplayName = "Original",
            MemoryScopes = new[] { ReleaseMemoryScopeKind.AllowOnce },
        };

        Assert.Equal(descriptor.Fingerprint, (descriptor with { SafeDisplayName = "Renamed" }).Fingerprint);
        Assert.NotEqual(descriptor.Fingerprint, CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Manifest, lineage, requestor, "other purpose"));
    }
}

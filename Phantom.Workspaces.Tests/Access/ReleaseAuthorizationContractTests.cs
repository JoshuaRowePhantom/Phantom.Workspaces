using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class ReleaseAuthorizationContractTests
{
    [Fact]
    public void ReleaseMemoryScope_AllUses_BindsOneLogicalCredential()
    {
        Assert.True(Enum.IsDefined(ReleaseMemoryScopeKind.AllUses));
        Assert.Equal(typeof(LogicalCredentialIdentifier),
            typeof(ReleaseMemoryDescriptor).GetProperty(nameof(ReleaseMemoryDescriptor.LogicalIdentifier))!.PropertyType);
        Assert.Equal(typeof(CredentialSourceIdentifier),
            typeof(ReleaseMemoryDescriptor).GetProperty(nameof(ReleaseMemoryDescriptor.SourceIdentifier))!.PropertyType);
    }

    [Fact]
    public void ReleasePolicyContract_FinalizeIdentity_CannotBroadenAuthorization()
    {
        var request = Request();
        var identity = new ResolvedCredentialIdentity
        {
            LogicalIdentifier = request.Credential.LogicalIdentifier,
            SourceIdentifier = request.SourceIdentifier,
            AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "stable-subject"),
            RequestIdentifier = request.RequestIdentifier,
            RequestGeneration = request.RequestGeneration,
        };

        Assert.True(identity.Narrows(request));
        Assert.False((identity with { RequestGeneration = 2 }).Narrows(request));
        Assert.False((identity with { SourceIdentifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.OAuth, "other") }).Narrows(request));
        Assert.False(identity.Narrows(request with
        {
            Credential = request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "different-subject"),
            },
        }));
    }

    [Fact]
    public void ReleaseMemoryDescriptor_ContainsNoMaterialOrUnsafeIdentity()
    {
        var allowed = new[]
        {
            nameof(ReleaseMemoryDescriptor.Identifier), nameof(ReleaseMemoryDescriptor.LogicalIdentifier),
            nameof(ReleaseMemoryDescriptor.SourceIdentifier), nameof(ReleaseMemoryDescriptor.AccountIdentifier),
            nameof(ReleaseMemoryDescriptor.Scope), nameof(ReleaseMemoryDescriptor.SafeDisplayName),
            nameof(ReleaseMemoryDescriptor.SafeContext),
        };
        Assert.Equal(allowed.Order(), typeof(ReleaseMemoryDescriptor).GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public void ReleaseMemoryDescriptor_SafeMetadata_ContainsOnlyAllowlistedProperties()
    {
        var properties = typeof(SafeApprovalContext).GetProperties();
        Assert.Equal(new[] { "Kind", "Origin", "Provider", "Source" },
            properties.Select(property => property.Name).Order());
        Assert.All(properties, property => Assert.True(property.PropertyType.IsEnum));
        var safe = new SafeApprovalContext
        {
            Provider = CredentialProviderKind.StoredSecret, Kind = CredentialKind.Secret,
            Source = CredentialSourceKind.SecretStore, Origin = CredentialUseKind.Mcp,
        };
        Assert.Contains("Saved secret", safe.ToString());
        Assert.Contains("Originally approved for: MCP access", safe.ToString());
        Assert.Equal("Details unavailable", (safe with { Provider = (CredentialProviderKind)999 }).ToString());
        Assert.Equal("Details unavailable", (safe with { Kind = (CredentialKind)999 }).ToString());
        Assert.Equal("Details unavailable", (safe with { Source = (CredentialSourceKind)999 }).ToString());
        Assert.Equal("Details unavailable", (safe with { Origin = (CredentialUseKind)999 }).ToString());
        foreach (var provider in Enum.GetValues<CredentialProviderKind>())
        foreach (var kind in Enum.GetValues<CredentialKind>())
        foreach (var source in Enum.GetValues<CredentialSourceKind>())
        foreach (var origin in Enum.GetValues<CredentialUseKind>())
        {
            var context = safe with { Provider = provider, Kind = kind, Source = source, Origin = origin };
            Assert.True(context.IsValid());
            Assert.DoesNotContain("Details unavailable", context.ToString());
            Assert.Contains("Originally approved for:", context.ToString());
        }
    }

    internal static ReleasePolicyRequest Request() => new()
    {
        RequestIdentifier = new() { Value = Guid.NewGuid() },
        RequestGeneration = 1,
        Credential = new()
        {
            LogicalIdentifier = new() { Digest = new string('a', 64) },
            Provider = CredentialProviderKind.OAuth,
            Kind = CredentialKind.AccessToken,
        },
        SourceIdentifier = new() { Digest = new string('b', 64) },
        RequestorIdentifier = new() { Digest = new string('c', 64) },
        Use = new()
        {
            Fingerprint = new() { Digest = new string('d', 64) },
            Kind = CredentialUseKind.Manifest,
            Lineage = new()
            {
                Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "one"),
                Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "one"),
                ManifestContent = CredentialIdentityFactory.CreateLineageIdentifier("content", "one"),
                Key = CredentialIdentityFactory.CreateLineageIdentifier("key", "one"),
                ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier("provider", "one"),
            },
            SafeDisplayName = "Use credential",
            MemoryScopes = Enum.GetValues<ReleaseMemoryScopeKind>(),
        },
    };
}

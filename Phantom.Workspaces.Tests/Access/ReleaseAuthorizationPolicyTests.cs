using System.IO;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class ReleaseAuthorizationPolicyTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-policy-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReleaseAuthorizationPolicy_EvaluateAsync_EveryScopeMatchesExpectedLineage()
    {
        foreach (var scope in Enum.GetValues<ReleaseMemoryScopeKind>())
        {
            var path = Path.Combine(this.directory, $"{scope}.json");
            var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var request = ReleaseAuthorizationContractTests.Request();
            request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
            var authorization = await policy.CommitAsync(new() { Request = request, Scope = scope }, TestContext.Current.CancellationToken);
            Assert.Equal(ReleaseAuthorizationValidation.Valid, await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
            var repeated = request with { RequestIdentifier = new() { Value = Guid.NewGuid() } };
            var reloaded = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var evaluation = await reloaded.EvaluateAsync(repeated, TestContext.Current.CancellationToken);
            if (scope == ReleaseMemoryScopeKind.AllowOnce)
            {
                Assert.IsType<ReleasePolicyRequired>(evaluation);
                Assert.False(File.Exists(path));
                Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch, await policy.ValidateAsync(authorization.Identifier, repeated, TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.IsType<ReleasePolicyMatched>(evaluation);
                Assert.IsType<ReleasePolicyRequired>(await reloaded.EvaluateAsync(repeated with
                {
                    Credential = repeated.Credential with { LogicalIdentifier = new() { Digest = new string('e', 64) } },
                }, TestContext.Current.CancellationToken));
                Assert.IsType<ReleasePolicyRequired>(await reloaded.EvaluateAsync(repeated with
                {
                    SourceIdentifier = new() { Digest = new string('e', 64) },
                }, TestContext.Current.CancellationToken));
                var changedLineage = repeated with
                {
                    Use = repeated.Use with
                    {
                        Fingerprint = new() { Digest = new string('f', 64) },
                        Lineage = new()
                        {
                            Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "two"),
                            Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "two"),
                            ManifestContent = CredentialIdentityFactory.CreateLineageIdentifier("content", "two"),
                            Key = CredentialIdentityFactory.CreateLineageIdentifier("key", "two"),
                            ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier("provider", "two"),
                        },
                    },
                };
                if (scope is ReleaseMemoryScopeKind.AllUses or ReleaseMemoryScopeKind.AnyManifest)
                {
                    Assert.IsType<ReleasePolicyMatched>(await reloaded.EvaluateAsync(changedLineage, TestContext.Current.CancellationToken));
                }
                else
                {
                    Assert.IsType<ReleasePolicyRequired>(await reloaded.EvaluateAsync(changedLineage, TestContext.Current.CancellationToken));
                }

                var lineageChanges = new (Func<CredentialUseLineage, CredentialUseLineage> Change, ReleaseMemoryScopeKind[] Affected)[]
                {
                    (lineage => lineage with { Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "other") },
                        new[] { ReleaseMemoryScopeKind.SessionIdentity, ReleaseMemoryScopeKind.KeyInSession }),
                    (lineage => lineage with { Manifest = CredentialIdentityFactory.CreateLineageIdentifier("manifest", "other") },
                        new[] { ReleaseMemoryScopeKind.ManifestIdentity, ReleaseMemoryScopeKind.McpProviderOnManifest }),
                    (lineage => lineage with { ManifestContent = CredentialIdentityFactory.CreateLineageIdentifier("content", "other") },
                        new[] { ReleaseMemoryScopeKind.ManifestContent, ReleaseMemoryScopeKind.KeyInManifestContent }),
                    (lineage => lineage with { Key = CredentialIdentityFactory.CreateLineageIdentifier("key", "other") },
                        new[] { ReleaseMemoryScopeKind.KeyInSession, ReleaseMemoryScopeKind.KeyInManifestContent, ReleaseMemoryScopeKind.KeyInAnyManifest }),
                    (lineage => lineage with { ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier("provider", "other") },
                        new[] { ReleaseMemoryScopeKind.McpProviderOnManifest }),
                };
                foreach (var change in lineageChanges)
                {
                    var changed = repeated with
                    {
                        Use = repeated.Use with
                        {
                            Fingerprint = new() { Digest = new string('e', 64) },
                            Lineage = change.Change(repeated.Use.Lineage),
                        },
                    };
                    var matches = scope != ReleaseMemoryScopeKind.ExactUse && !change.Affected.Contains(scope);
                    Assert.Equal(matches, await reloaded.EvaluateAsync(changed, TestContext.Current.CancellationToken) is ReleasePolicyMatched);
                }

                Assert.IsType<ReleasePolicyMatched>(await reloaded.EvaluateAsync(repeated with
                {
                    Use = repeated.Use with { SafeDisplayName = "A renamed safe display label" },
                }, TestContext.Current.CancellationToken));
                Assert.Equal(scope == ReleaseMemoryScopeKind.AllUses, await reloaded.EvaluateAsync(repeated with
                {
                    RequestorIdentifier = new() { Digest = new string('e', 64) },
                }, TestContext.Current.CancellationToken) is ReleasePolicyMatched);
            }
        }
    }

    [Fact]
    public async Task ReleaseAuthorizationPolicy_FinalizeIdentity_NarrowsProvisionalAccount()
    {
        var path = Path.Combine(this.directory, "memory.json");
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        var request = ReleaseAuthorizationContractTests.Request();
        var authorization = await policy.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses }, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
        Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch, await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        var identity = new ResolvedCredentialIdentity
        {
            LogicalIdentifier = request.Credential.LogicalIdentifier,
            SourceIdentifier = request.SourceIdentifier,
            AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "private-account"),
            RequestIdentifier = request.RequestIdentifier,
            RequestGeneration = request.RequestGeneration,
        };
        await Assert.ThrowsAsync<ReleaseIdentityMismatchException>(async () =>
            await policy.FinalizeCredentialIdentityAsync(authorization.Identifier, identity with { RequestGeneration = 10 }, TestContext.Current.CancellationToken));
        await policy.FinalizeCredentialIdentityAsync(authorization.Identifier, identity, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseAuthorizationValidation.Valid, await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        Assert.IsType<ReleasePolicyRequired>(await policy.EvaluateAsync(request, TestContext.Current.CancellationToken));
        Assert.IsType<ReleasePolicyMatched>(await policy.EvaluateAsync(request with
        {
            Credential = request.Credential with { AccountIdentifier = identity.AccountIdentifier },
        }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReleaseIdentityMismatchException>(async () =>
            await policy.FinalizeCredentialIdentityAsync(authorization.Identifier, identity with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "another-account"),
            }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-account", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(request.Use.SafeDisplayName, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseAuthorizationPolicy_AnonymousTunnel_PreservesAccountlessAuthorizationAndForget()
    {
        var path = Path.Combine(this.directory, "anonymous.json");
        await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with
        {
            Credential = request.Credential with
            {
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.Anonymous,
            },
        };
        var authorization = await policy.CommitAsync(new()
        {
            Request = request,
            Scope = ReleaseMemoryScopeKind.AllUses,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseAuthorizationValidation.Valid,
            await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        var memory = Assert.Single(await policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.Null(memory.AccountIdentifier);
        await using var reopened = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        Assert.IsType<ReleasePolicyMatched>(await reopened.EvaluateAsync(
            request with { RequestIdentifier = new() { Value = Guid.NewGuid() } },
            TestContext.Current.CancellationToken));
        await policy.ForgetAsync(memory.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseAuthorizationValidation.AuthorizationRequired,
            await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CredentialProviderKind.GitHub, CredentialKind.Anonymous)]
    [InlineData(CredentialProviderKind.DevTunnel, CredentialKind.IdentityToken)]
    [InlineData(CredentialProviderKind.DevTunnel, CredentialKind.ConnectToken)]
    public async Task ReleaseAuthorizationPolicy_IdentityBearingCredential_StillRequiresActualAccount(
        CredentialProviderKind provider,
        CredentialKind kind)
    {
        var path = Path.Combine(this.directory, "provisional.json");
        await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with { Credential = request.Credential with { Provider = provider, Kind = kind } };
        var authorization = await policy.CommitAsync(new()
        {
            Request = request,
            Scope = ReleaseMemoryScopeKind.AllUses,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch,
            await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        Assert.Empty(await policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ReleaseAuthorizationPolicy_ForgetAsync_NextValidationRequiresAuthorization()
    {
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json")));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
        var authorization = await policy.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.ExactUse }, TestContext.Current.CancellationToken);
        var memory = Assert.Single(await policy.ListAsync(TestContext.Current.CancellationToken));
        await policy.ForgetAsync(memory.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseAuthorizationValidation.AuthorizationRequired, await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        Assert.IsType<ReleasePolicyRequired>(await policy.EvaluateAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(await policy.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseAuthorizationPolicy_DisposeAsync_RevokesLiveAuthorizationWithoutForgettingMemory()
    {
        var path = Path.Combine(this.directory, "memory.json");
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
        var authorization = await policy.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses }, TestContext.Current.CancellationToken);
        await policy.DisposeAsync();
        await policy.DisposeAsync();
        Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch,
            await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        var reopened = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
        Assert.IsType<ReleasePolicyMatched>(await reopened.EvaluateAsync(request, TestContext.Current.CancellationToken));
    }
}

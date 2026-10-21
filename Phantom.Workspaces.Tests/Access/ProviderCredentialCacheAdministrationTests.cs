using System.IO;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class ProviderCredentialCacheAdministrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-provider-admin-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoredSecretAdministration_DeletesExactOwnedGenerationWithoutReadingSourceOrForgettingPolicy()
    {
        var source = new StoredSource();
        var executor = new StoredSecretAccessExecutor(source);
        var (broker, registry) = this.Create<StoredSecretAccessRequest, StoredSecretAccessResult>(executor);
        await using var owned = broker;
        var request = StoredSecretAccessExecutorTests.Request();
        request = request with { Use = request.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllUses, ReleaseMemoryScopeKind.AllowOnce] } };
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await using var first = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await firstTask).Result;
        ICredentialCacheAdministration administration = executor;
        var original = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CredentialProviderKind.StoredSecret, original.Provider);
        Assert.Equal(CredentialKind.Secret, original.Kind);
        Assert.Equal(original.Identifier, Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken)).Identifier);
        Assert.Equal(1, source.Reads);
        source.Material = "stored-rotated-material";
        await using var second = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(
            await broker.AcquireAsync(request, TestContext.Current.CancellationToken)).Result;
        var stale = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            administration.DeleteAsync(original.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, stale.Failure);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var current = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(original.Identifier, current.Identifier);
        await administration.DeleteAsync(current.Identifier, TestContext.Current.CancellationToken);
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, source.Reads);
        Assert.Single(await broker.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubAdministration_StaleDeleteCannotDeleteRenewalAndCurrentDeleteInvalidatesLeaseOnly()
    {
        var source = new GitHubSource();
        var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = this.Create<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
        await using var owned = broker;
        var request = GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli);
        request = request with { Use = request.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllUses, ReleaseMemoryScopeKind.AllowOnce] } };
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        ICredentialCacheAdministration administration = executor;
        var original = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CredentialProviderKind.GitHub, original.Provider);
        Assert.Equal(CredentialKind.AccessToken, original.Kind);
        await executor.GetGenerationCache(original.SourceIdentifier).InvalidateGenerationAsync(original.Generation, TestContext.Current.CancellationToken);
        source.Material = "github-renewed-material";
        var secondTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, source.Acquisitions);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await secondTask).Result;
        var stale = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            administration.DeleteAsync(original.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, stale.Failure);
        var current = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(original.Generation, current.Generation);
        Assert.Equal(2, source.Acquisitions);
        Assert.DoesNotContain("github-renewed-material", System.Text.Json.JsonSerializer.Serialize(current));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var remembered = (await broker.Policy.ListAsync(TestContext.Current.CancellationToken)).Select(memory => memory.Identifier).ToArray();
        await administration.DeleteAsync(current.Identifier, TestContext.Current.CancellationToken);
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, source.Acquisitions);
        Assert.Equal(remembered, (await broker.Policy.ListAsync(TestContext.Current.CancellationToken)).Select(memory => memory.Identifier).ToArray());
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CredentialProviderKind.StoredSecret)]
    [InlineData(CredentialProviderKind.GitHub)]
    public async Task Administration_UnknownIdentifierAndDisposedProviderFailSafely(CredentialProviderKind provider)
    {
        IAsyncDisposable executor = provider == CredentialProviderKind.StoredSecret
            ? new StoredSecretAccessExecutor(new StoredSource())
            : new GitHubCredentialAccessExecutor(new GitHubSource());
        ICredentialCacheAdministration administration = (ICredentialCacheAdministration)executor;
        var unknown = new CredentialCacheIdentifier { Value = Guid.NewGuid() };
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            administration.DeleteAsync(unknown, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Unavailable, missing.Failure);
        await executor.DisposeAsync();
        var disposed = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            administration.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Unavailable, disposed.Failure);
        await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            administration.DeleteAsync(unknown, TestContext.Current.CancellationToken).AsTask());
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private (AccessBroker Broker, UserAuthorizationRegistry Registry) Create<TRequest, TResult>(
        ICredentialAccessExecutor<TRequest, TResult> executor)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        var registry = new UserAuthorizationRegistry();
        var registrations = new CredentialAccessExecutorRegistry();
        registrations.Add<TRequest, TResult>(executor);
        return (new AccessBroker(
            new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json"))),
            registrations,
            new ProviderAcquisitionCoordinator(),
            registry), registry);
    }

    private static async Task AllowAsync(
        UserAuthorizationRegistry registry,
        CredentialSourceKind source)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SourceOptions.Single(option => option.Kind == source).Identifier,
        }, TestContext.Current.CancellationToken);
    }

    private sealed class StoredSource : IStoredSecretAccessSource
    {
        public int Reads { get; private set; }
        public string Material { get; set; } = "stored-initial-material";
        public ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            Assert.Equal(CredentialSourceKind.Environment, source);
            this.Reads++;
            return ValueTask.FromResult<char[]?>(this.Material.ToCharArray());
        }
    }

    private sealed class GitHubSource : IGitHubCredentialAccessSource
    {
        public int Acquisitions { get; private set; }
        public string Material { get; set; } = "github-initial-material";
        public bool EnvironmentExists() => false;

        public ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.Equal(CredentialSourceKind.GitHubCli, source);
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            this.Acquisitions++;
            return ValueTask.FromResult<GitHubCredentialMaterial?>(new GitHubCredentialMaterial
            {
                Material = this.Material.ToCharArray(),
                Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "admin-provider-principal"),
            });
        }
    }
}

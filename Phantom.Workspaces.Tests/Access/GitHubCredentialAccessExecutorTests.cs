using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubCredentialAccessExecutorTests
{
    [Fact]
    public async Task GitHubExecutor_InspectAsync_DoesNotReadEnvironmentOrInvokeCli()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var inspection = await executor.InspectAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(2, inspection.Sources.Count);
        Assert.Empty(source.Acquisitions);
        Assert.Empty(inspection.CachedGenerations);
        Assert.All(inspection.Sources, choice => Assert.Equal(CredentialSourceUsability.AcquisitionRequired, choice.Usability));
    }

    [Theory]
    [InlineData(CredentialSourceKind.Environment)]
    [InlineData(CredentialSourceKind.GitHubCli)]
    public async Task GitHubExecutor_ExecuteAsync_GhCliRunsOnlyAfterReleaseCommit(CredentialSourceKind selected)
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(selected), TestContext.Current.CancellationToken);
        Assert.Empty(source.Acquisitions);
        await AllowAsync(registry, selected);
        await using var result = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await task).Result;
        Assert.Equal(new[] { selected }, source.Acquisitions);
        var use = await result.Lease.UseAsync((value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken);
        Assert.Equal("private-test-token", Assert.IsType<SensitiveValueUseSucceeded<string>>(use).Result);
        Assert.DoesNotContain("private-test-token", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
        Assert.DoesNotContain("private-account", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
    }

    [Fact]
    public async Task GitHubExecutor_ResolvedAccountMismatch_FailsClosedWithoutLoggingToken()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var task = broker.AcquireAsync(request with
        {
            Credential = request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "different-account"),
            },
        }, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var failure = Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await task);
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.DoesNotContain("private", failure.SafeMessage);
        Assert.Null(await executor.GetGenerationCache(request.Sources[0].Identifier).InspectGenerationAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubExecutor_ForcedEnvironmentMissing_DoesNotFallbackToCli()
    {
        var source = new Source { HasEnvironment = false };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(CredentialSourceKind.Environment), TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await task);
        Assert.Equal(new[] { CredentialSourceKind.Environment }, source.Acquisitions);
    }

    [Fact]
    public async Task GitHubExecutor_CacheDeletion_InvalidatesOldLeasesButNotIndependentCallerOwnership()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        var secondTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await secondTask).Result;
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Single(source.Acquisitions);
        var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
        var original = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(original.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var nextTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var next = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await nextTask).Result;
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await cache.InvalidateGenerationAsync(original.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await next.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    internal static GitHubCredentialAccessRequest Request(CredentialSourceKind? source = null)
    {
        var request = GitHubCredentialAccessRequest.Create(
            GitHubCredentialPurpose.Repository,
            new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "github-tests"),
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            },
            new CredentialUseLineage(),
            "read",
            source: source);
        return request with
        {
            Use = request.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce] },
        };
    }

    private static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(GitHubCredentialAccessExecutor executor)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore()), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static async Task AllowAsync(
        UserAuthorizationRegistry registry,
        CredentialSourceKind selected)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SourceOptions.Single(source => source.Kind == selected).Identifier,
        }, TestContext.Current.CancellationToken);
    }

    private sealed class Source : IGitHubCredentialAccessSource
    {
        public bool HasEnvironment { get; init; } = true;
        public List<CredentialSourceKind> Acquisitions { get; } = [];

        public bool EnvironmentExists() => this.HasEnvironment;

        public ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            this.Acquisitions.Add(source);
            return ValueTask.FromResult(source == CredentialSourceKind.Environment && !this.HasEnvironment ? null : new GitHubCredentialMaterial
            {
                Material = "private-test-token".ToCharArray(),
                Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private-account"),
            });
        }
    }
}

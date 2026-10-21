using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubSelectedSourcePolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EnvironmentAppearsWhilePending_SelectedVerifiedCliIdentityRemainsAuthorized(
        bool explicitPin, bool retireCachedGeneration)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-selected-github-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "cli-a");
            var source = new Source { Account = accountA, HasEnvironment = false };
            var executor = new GitHubCredentialAccessExecutor(source);
            var policy = new ObservedPolicy(new ReleaseAuthorizationPolicy(
                new ReleaseMemoryStore(Path.Combine(directory, "memory.json"))));
            var registry = new UserAuthorizationRegistry();
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
            await using var broker = new AccessBroker(policy, executors, new ProviderAcquisitionCoordinator(), registry);
            var warm = broker.AcquireAsync(Request(CredentialSourceKind.GitHubCli), TestContext.Current.CancellationToken);
            await ApproveCli();
            await using var initial = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(
                await warm.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
            if (retireCachedGeneration)
            {
                var cached = Assert.Single(await executor.ListAsync(TestContext.Current.CancellationToken));
                await executor.DeleteAsync(cached.Identifier, TestContext.Current.CancellationToken);
            }
            var request = Request(null);
            request = request with
            {
                Credential = request.Credential with { AccountIdentifier = explicitPin ? accountA : null },
            };
            var cli = request.Sources.Single(choice => choice.Kind == CredentialSourceKind.GitHubCli).Identifier;
            var before = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(accountA, before.Credential.AccountIdentifier);
            Assert.Equal(accountA, before.AcquisitionKeys[cli].Account);
            Assert.Equal(!retireCachedGeneration, before.CachedGenerations.ContainsKey(cli));
            ReleasePolicyRequest? recoveryPolicyRequest = null;
            policy.AfterCommit = (decision, _, _) =>
            {
                recoveryPolicyRequest = decision.Request;
                return ValueTask.CompletedTask;
            };
            var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            source.HasEnvironment = true;
            var after = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(explicitPin ? accountA : null, after.Credential.AccountIdentifier);
            Assert.Equal(before.Credential with { AccountIdentifier = null },
                after.Credential with { AccountIdentifier = null });
            if (retireCachedGeneration)
            {
                Assert.False(after.CachedGenerations.ContainsKey(cli));
            }
            else
            {
                Assert.Equal(before.CachedGenerations[cli], after.CachedGenerations[cli]);
            }
            Assert.Equal(before.AcquisitionKeys[cli], after.AcquisitionKeys[cli]);
            await broker.RefreshPendingSourcesAsync(TestContext.Current.CancellationToken);
            await ApproveCli();
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(outcome is AccessSucceeded<GitHubCredentialAccessResult>,
                $"Expected selected CLI recovery. Actual: {outcome}; policy committed: {recoveryPolicyRequest is not null}.");
            await using var recovered = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(outcome).Result;
            Assert.Equal(accountA, recoveryPolicyRequest!.Credential.AccountIdentifier);
            Assert.Equal(retireCachedGeneration ? 2 : 1, source.Acquisitions.Count);
            Assert.All(source.Acquisitions, kind => Assert.Equal(CredentialSourceKind.GitHubCli, kind));
            Assert.Empty(await policy.ListAsync(TestContext.Current.CancellationToken));

            Task ApproveCli()
            {
                var current = registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending);
                return registry.ActAsync(new ApplyUserAuthorizationAction
                {
                    RequestIdentifier = current.Identifier,
                    ExpectedRevision = current.Revision,
                    Source = current.SourceOptions.Single(choice => choice.Kind == CredentialSourceKind.GitHubCli).Identifier,
                    Memory = current.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllowOnce).Identifier,
                }, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 0)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 0)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 1)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 1)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 2)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 2)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 3)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 3)]
    public async Task MissingEnvironment_CommitsOnlyItsOwnIdentityBeforeActualExecutorFails(
        ReleaseMemoryScopeKind scope, int identity)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-selected-github-source-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "environment-a");
            var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "cli-b");
            var source = new Source { Account = accountA };
            var executor = new GitHubCredentialAccessExecutor(source);
            var policy = new ObservedPolicy(new ReleaseAuthorizationPolicy(
                new ReleaseMemoryStore(Path.Combine(directory, "memory.json"))));
            var registry = new UserAuthorizationRegistry();
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
            await using var broker = new AccessBroker(policy, executors, new ProviderAcquisitionCoordinator(), registry);

            if (identity is 1 or 3)
            {
                await WarmAsync(CredentialSourceKind.Environment);
            }
            source.Account = accountB;
            await WarmAsync(CredentialSourceKind.GitHubCli);
            source.HasEnvironment = false;
            var request = Request(identity == 3 ? CredentialSourceKind.Environment : null);
            request = request with
            {
                Credential = request.Credential with { AccountIdentifier = identity == 2 ? accountA : null },
                Use = request.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] },
            };
            var environment = request.Sources.Single(choice => choice.Kind == CredentialSourceKind.Environment).Identifier;
            var expected = identity == 0 ? null : accountA;
            var inspected = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(identity is 2 or 3 ? accountA : accountB, inspected.Credential.AccountIdentifier);
            Assert.Equal(CredentialSourceUsability.Missing,
                inspected.Sources.Single(choice => choice.Identifier == environment).Usability);
            Assert.Equal(expected, inspected.AcquisitionKeys[environment].Account);
            Assert.False(inspected.CachedGenerations.ContainsKey(environment));
            Assert.Empty(await policy.ListAsync(TestContext.Current.CancellationToken));

            var committed = new TaskCompletionSource<(ReleasePolicyDecision Decision, ReleaseAuthorization Authorization)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var failProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            policy.AfterCommit = async (decision, authorization, token) =>
            {
                committed.TrySetResult((decision, authorization));
                await failProvider.Task.WaitAsync(token);
            };
            var callsBefore = source.Acquisitions.Count;
            var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
            var row = registry.GetSnapshot().Single(snapshot => snapshot.State == UserAuthorizationState.Pending);
            await registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Source = environment,
                Memory = row.MemoryOptions.Single(option => option.Scope == scope).Identifier,
            }, TestContext.Current.CancellationToken);
            var observed = await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(environment, observed.Decision.Request.SourceIdentifier);
            Assert.Equal(expected, observed.Decision.Request.Credential.AccountIdentifier);
            Assert.Equal(ReleaseAuthorizationValidation.Valid,
                await policy.ValidateAcquisitionAuthorizationAsync(observed.Authorization, observed.Decision.Request,
                    TestContext.Current.CancellationToken));
            var memories = await policy.ListAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(memories, memory =>
                memory.SourceIdentifier == environment && memory.AccountIdentifier == accountB);
            if (expected is null)
            {
                Assert.Empty(memories);
                Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch, await policy.ValidateAsync(
                    observed.Authorization.Identifier, observed.Decision.Request, TestContext.Current.CancellationToken));
            }
            else
            {
                var memory = Assert.Single(memories);
                Assert.Equal(environment, memory.SourceIdentifier);
                Assert.Equal(accountA, memory.AccountIdentifier);
                Assert.Equal(scope, memory.Scope);
            }
            Assert.False(pending.IsCompleted);
            Assert.Equal(callsBefore, source.Acquisitions.Count);
            failProvider.TrySetResult();
            Assert.IsType<AccessReacquisitionRequired<GitHubCredentialAccessResult>>(
                await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(callsBefore, source.Acquisitions.Count);
            Assert.Equal(expected is null ? 0 : 1, (await policy.ListAsync(TestContext.Current.CancellationToken)).Count);

            async Task WarmAsync(CredentialSourceKind kind)
            {
                var warm = broker.AcquireAsync(Request(kind), TestContext.Current.CancellationToken);
                var warmRow = registry.GetSnapshot().Single(snapshot => snapshot.State == UserAuthorizationState.Pending);
                await registry.ActAsync(new ApplyUserAuthorizationAction
                {
                    RequestIdentifier = warmRow.Identifier,
                    ExpectedRevision = warmRow.Revision,
                    Source = warmRow.SelectedSource,
                    Memory = warmRow.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllowOnce).Identifier,
                }, TestContext.Current.CancellationToken);
                await using var warmed = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(
                    await warm.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static GitHubCredentialAccessRequest Request(CredentialSourceKind? source) =>
        GitHubCredentialAccessRequest.Create(
            GitHubCredentialPurpose.Repository,
            new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "selected-source-tests"),
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            }, new CredentialUseLineage(), "read", source: source);

    private sealed class Source : IGitHubCredentialAccessSource
    {
        public bool HasEnvironment { get; set; } = true;
        public required CredentialAccountIdentifier Account { get; set; }
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
            return ValueTask.FromResult<GitHubCredentialMaterial?>(new()
            {
                Account = this.Account,
                Material = "deterministic-test-material".ToCharArray(),
            });
        }
    }

    private sealed class ObservedPolicy(ReleaseAuthorizationPolicy policy)
        : IReleaseAuthorizationPolicy, IProviderAcquisitionAuthorizationPolicy, IAsyncDisposable
    {
        public Func<ReleasePolicyDecision, ReleaseAuthorization, CancellationToken, ValueTask>? AfterCommit { get; set; }
        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.EvaluateAsync(request, cancellationToken);
        public async ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken = default)
        {
            var authorization = await policy.CommitAsync(decision, cancellationToken);
            if (this.AfterCommit is { } afterCommit)
            {
                await afterCommit(decision, authorization, cancellationToken);
            }
            return authorization;
        }
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
            ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.ValidateAsync(identifier, request, cancellationToken);
        public ValueTask<ReleaseAuthorizationValidation> ValidateAcquisitionAuthorizationAsync(
            ReleaseAuthorization authorization, ReleasePolicyRequest request, CancellationToken cancellationToken) =>
            ((IProviderAcquisitionAuthorizationPolicy)policy).ValidateAcquisitionAuthorizationAsync(authorization, request, cancellationToken);
        public ValueTask FinalizeCredentialIdentityAsync(
            ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken = default) =>
            policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) =>
            policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => policy.DisposeAsync();
    }
}

using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubPrivateCredentialSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PrivateMaterialUse_RejectsExpiredRawWrongExecutorAndWrongKeyProofs(int proofKind)
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        var context = parent.Context!;
        var key = new ProviderAcquisitionKey
        {
            Configuration = context.PolicyRequest!.Credential.LogicalIdentifier,
            Source = context.PolicyRequest.SourceIdentifier,
            Account = context.PolicyRequest.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };
        var proof = parent.Permit!;
        if (proofKind != 0)
        {
            var candidate = new ProviderInteractionPermit(
                context.RequestIdentifier, context.Revision,
                proofKind == 2 ? CredentialExecutorKind.GitHub : CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration,
                proofKind == 3 ? key with { Configuration = new() { Digest = new string('e', 64) } } : key,
                () => true);
            proof = proofKind == 1 ? candidate : candidate.BindExecution(() => true);
        }
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(await provider.UseGenerationAsync<int>(
            inspection.Source, inspection.CachedGeneration!.Generation, context, proof,
            (_, _, _) => throw new InvalidOperationException("An invalid parent proof must not expose identity material."),
            TestContext.Current.CancellationToken));
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(await provider.UsePrivateAsync<int>(
            parent.IdentityRequest, context, proof,
            (_, _) => throw new InvalidOperationException("The combined route must require the same consumed parent proof."),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateAcquisition_StaleOriginallyUnpinnedHandleCannotEraseRetainedPrincipal()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var identityRequest = new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli };
        var unpinned = await provider.InspectPrivateAsync(identityRequest, TestContext.Current.CancellationToken);
        Assert.Null(unpinned.Source.ExpectedAccountIdentifier);
        Assert.Null(unpinned.Source.RetainedAccountIdentifier);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var first = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await first).Result.DisposeAsync();
        var accountA = source.Account;
        var cache = provider.GetGenerationCache(unpinned.Source.Identifier);
        var generation = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        await cache.InvalidateGenerationAsync(generation, TestContext.Current.CancellationToken);
        source.Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "stale-handle-principal-B");
        parent.BodyOverride = (context, _, _) =>
        {
            Assert.Throws<ReleaseIdentityMismatchException>(() => provider.CreatePrivateAcquisition(identityRequest, unpinned.Source, context));
            return ValueTask.FromResult<ProviderAcquisitionOutcome>(new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch });
        };
        var second = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<ParentResult>>(await second).ErrorCode);
        Assert.Equal(1, source.Acquisitions);
        Assert.Null(await cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(accountA, (await provider.InspectPrivateAsync(identityRequest, TestContext.Current.CancellationToken)).Source.ExpectedAccountIdentifier);
    }

    [Fact]
    public async Task PrivateAcquisition_PrincipalChangesWhileProviderRuns_CannotCommitUsingUnpinnedAdmittedKey()
    {
        var events = new List<string>();
        var source = new Source(events)
        {
            Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private-inflight-principal-B"),
        };
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "concurrent-approved-principal-A");
        source.BeforeReturn = async cancellationToken =>
        {
            source.BeforeReturn = null;
            source.Account = accountA;
            var request = GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli);
            var accepted = broker.AcquireAsync(request with
            {
                Credential = request.Credential with { AccountIdentifier = accountA },
            }, cancellationToken);
            await AllowAsync(registry);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await accepted).Result.DisposeAsync();
        };
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<ParentResult>>(await task).ErrorCode);
        Assert.Null(parent.UsedAccount);
        Assert.DoesNotContain("mint", events);
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        Assert.Equal(accountA, inspection.Source.ExpectedAccountIdentifier);
        Assert.Equal(accountA, inspection.CachedGeneration!.AccountIdentifier);
        Assert.Equal(2, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_MetadataIsSafeAndRealPrivateCoordinatorProofCommitsOneGeneration()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var metadata = await provider.InspectPrivateAsync(
            new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli },
            TestContext.Current.CancellationToken);
        Assert.Null(metadata.CachedGeneration);
        Assert.Equal(CredentialSourceUsability.AcquisitionRequired, metadata.Usability);
        Assert.Equal(metadata.Source.Identifier, metadata.AcquisitionKey.Source);
        Assert.Equal(GitHubCredentialAccessRequest.CreateLogicalIdentifier(), metadata.AcquisitionKey.Configuration);
        Assert.Equal(0, source.Acquisitions);
        Assert.Empty(events);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "identity acquired", "mint", "connect committed" }, events);
        Assert.Equal(source.Account, parent.UsedAccount);
        var cached = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        Assert.Equal(parent.UsedGeneration, cached.CachedGeneration!.Generation);
        Assert.Equal(parent.UsedAccount, cached.Source.ExpectedAccountIdentifier);
        Assert.Equal(CredentialSourceUsability.Usable, cached.Usability);
        Assert.Single(registry.GetSnapshot());
        Assert.DoesNotContain("identity-material", System.Text.Json.JsonSerializer.Serialize(cached));
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_ExactGenerationCannotSubstituteRotatedAccountBeforeMint()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "split-principal-B");
        parent.BeforeGenerationUse = async (inspection, generation, cancellationToken) =>
        {
            var cache = provider.GetGenerationCache(inspection.Source.Identifier);
            Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
                await cache.InvalidateGenerationAsync(generation, cancellationToken));
            source.Account = accountB;
            source.Material = "identity-material-B";
            var request = GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli);
            var rotated = broker.AcquireAsync(request with
            {
                Credential = request.Credential with { AccountIdentifier = accountB },
            }, cancellationToken);
            await AllowAsync(registry);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await rotated).Result.DisposeAsync();
            var current = (await cache.InspectGenerationAsync(cancellationToken))!;
            Assert.NotEqual(generation, current.Generation);
            Assert.Equal(accountB, current.AccountIdentifier);
        };
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        var failure = Assert.IsType<AccessFailed<ParentResult>>(await task);
        Assert.Equal(AccessErrorCode.InvalidGeneration, failure.ErrorCode);
        Assert.Null(parent.UsedAccount);
        Assert.DoesNotContain("mint", events);
        Assert.DoesNotContain("connect committed", events);
        Assert.Equal(2, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_AccountPinMismatchCannotUseCachedGenerationOrRefresh()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        parent.BeforeGenerationUse = async (_, generation, cancellationToken) =>
        {
            var pinB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "different-private-principal");
            var inspection = await provider.InspectPrivateAsync(new GitHubPrivateCredentialRequest
            {
                Source = CredentialSourceKind.GitHubCli,
                AccountIdentifier = pinB,
            }, cancellationToken);
            Assert.Equal(CredentialSourceUsability.AcquisitionRequired, inspection.Usability);
            Assert.Equal(pinB, inspection.AcquisitionKey.Account);
            Assert.Equal(source.Account, inspection.CachedGeneration!.AccountIdentifier);
            var outcome = await provider.UseGenerationAsync<int>(
                inspection.Source, generation, parent.Context!, parent.Permit!,
                (_, _, _) => throw new InvalidOperationException("A mismatched principal must not reach the operation."),
                cancellationToken);
            Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<GitHubPrivateCredentialUseFailed<int>>(outcome).ErrorCode);
        };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_CallbackFailureDoesNotDisposeProviderCacheOrRefresh()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        parent.BeforeGenerationUse = async (inspection, generation, cancellationToken) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UseGenerationAsync<int>(
                inspection.Source, generation, parent.Context!, parent.Permit!,
                (_, _, _) => ValueTask.FromException<int>(new InvalidOperationException("Private operation failed.")),
                cancellationToken).AsTask());
            var next = await provider.UseGenerationAsync(
                inspection.Source, generation, parent.Context!, parent.Permit!, (_, account, _) =>
                {
                    Assert.Equal(source.Account, account);
                    return ValueTask.FromResult(1);
                }, cancellationToken);
            Assert.Equal(1, Assert.IsType<GitHubPrivateCredentialUseSucceeded<int>>(next).Result);
        };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_UnconsumedProofCannotExecuteProviderAcquisition()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        var operation = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, parent.Context!);
        Assert.Equal(inspection.AcquisitionKey, operation.CompatibilityKey);
        var unconsumed = new ProviderInteractionPermit(
            operation.RequestIdentifier, operation.Revision, CredentialExecutorKind.GitHub,
            operation.AcquisitionGeneration, operation.CompatibilityKey, () => true);
        using var gate = new SemaphoreSlim(1, 1);
        var commit = new ProviderAcquisitionCommitContext(() => true, TestContext.Current.CancellationToken, gate, unconsumed);
        Assert.IsType<ProviderAcquisitionDenied>(await operation.ExecuteAsync(commit, TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ConnectReleasePrecedesIdentityAndMint_WithoutIdentityExportRow()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        Assert.Equal(0, source.Acquisitions);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "identity acquired", "mint", "connect committed" }, events);
        Assert.Single(registry.GetSnapshot());
        Assert.Single(await provider.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await broker.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("identity-material", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
    }

    [Fact]
    public async Task PrivateGitHubIdentity_CachedReuseStillRequiresIndependentConnectRelease()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var first = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await first).Result.DisposeAsync();
        events.Clear();
        var second = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        Assert.False(second.IsCompleted);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await second).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "mint", "connect committed" }, events);
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(2, registry.GetSnapshot().Count);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_DeniedConnect_DoesNotAcquireIdentityOrMint()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(provider, new ParentExecutor(provider, events));
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<ParentResult>>(await task);
        Assert.Empty(events);
        Assert.Equal(0, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ExpiredParentProof_CannotUseCachedMaterial()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var outcome = await provider.UsePrivateAsync<int>(
            new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli },
            parent.Context!,
            parent.Permit!,
            (_, _) => throw new InvalidOperationException("Expired proof must not expose private material."),
            TestContext.Current.CancellationToken);
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(outcome);
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ExportedIdentityContext_CannotUsePrivateConnectRoute()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(provider, new ParentExecutor(provider, events));
        await using var owned = broker;
        var request = Request();
        var task = broker.AcquireAsync(request with
        {
            Credential = request.Credential with { Kind = CredentialKind.IdentityToken },
        }, TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.IsType<AccessDenied<ParentResult>>(await task);
        Assert.Equal(new[] { "release validated" }, events);
        Assert.Equal(0, source.Acquisitions);
    }

    private static ParentRequest Request()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "private-connect-test"),
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Tunnel",
        };
        var lineage = new CredentialUseLineage();
        return new ParentRequest
        {
            Requestor = requestor,
            Use = new CredentialUseDescriptor
            {
                Kind = CredentialUseKind.Tunnel,
                Lineage = lineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Tunnel, lineage, requestor.Identifier, "connect"),
                SafeDisplayName = "Connect tunnel",
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Credential = new CredentialReference
            {
                LogicalIdentifier = new() { Digest = new string('c', 64) },
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.DevTunnel, "connect-account"),
            },
            Sources =
            [
                new CredentialSourceChoiceDescriptor
                {
                    Identifier = new() { Digest = new string('d', 64) },
                    Kind = CredentialSourceKind.Provider,
                    Provider = CredentialProviderKind.DevTunnel,
                    Usability = CredentialSourceUsability.AcquisitionRequired,
                    SafeDisplayName = "Tunnel provider",
                },
            ],
        };
    }

    private static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(
        GitHubCredentialAccessExecutor provider,
        ParentExecutor parent)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(provider);
        executors.Add<ParentRequest, ParentResult>(parent);
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore()), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static async Task AllowAsync(UserAuthorizationRegistry registry)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);
    }

    private sealed record ParentRequest : CredentialAccessRequest<ParentResult>;

    private sealed class ParentResult : ICredentialAccessResult
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ParentExecutor(
        GitHubCredentialAccessExecutor provider,
        List<string> events) : ICredentialAccessExecutor<ParentRequest, ParentResult>
    {
        private string? derivedMaterial;
        public ICredentialExecutionContext? Context { get; private set; }
        public ProviderInteractionPermit? Permit { get; private set; }
        public bool UseSplitBoundary { get; init; }
        public GitHubPrivateCredentialRequest IdentityRequest { get; init; } = new() { Source = CredentialSourceKind.GitHubCli };
        public CredentialAccountIdentifier? UsedAccount { get; private set; }
        public CredentialMaterialGenerationIdentifier? UsedGeneration { get; private set; }
        public Func<GitHubPrivateCredentialInspection, CredentialMaterialGenerationIdentifier, CancellationToken, ValueTask>? BeforeGenerationUse { get; set; }
        public Func<ICredentialExecutionContext, ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>>? BodyOverride { get; set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelConnect;

        public ValueTask<CredentialInspection> InspectAsync(ParentRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential,
            Sources = request.Sources,
            AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
            {
                [request.Sources[0].Identifier] = new()
                {
                    Configuration = request.Credential.LogicalIdentifier,
                    Source = request.Sources[0].Identifier,
                    Account = request.Credential.AccountIdentifier,
                    Protocol = ProviderProtocolKind.TunnelConnect,
                },
            },
        });

        public async ValueTask<CredentialExecutionOutcome<ParentResult>> ExecuteAsync(
            ParentRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ReleaseAuthorizationValidation.Valid, await context.Policy.ValidateAsync(authorization.Identifier, context.PolicyRequest!, cancellationToken));
            events.Add("release validated");
            Assert.NotNull(interactionPermit);
            var operation = new Operation(this, provider, events, context, inspection.AcquisitionKeys[inspection.SelectedSource!]);
            var acquired = await context.ExecuteProviderAcquisitionAsync(operation, interactionPermit, cancellationToken);
            if (acquired is ProviderAcquisitionDenied)
            {
                return new CredentialExecutionDenied<ParentResult>();
            }
            if (acquired is ProviderAcquisitionFailed failed)
            {
                return new CredentialExecutionFailed<ParentResult> { ErrorCode = failed.ErrorCode, CanRetry = true };
            }
            Assert.IsType<ProviderAcquisitionSucceeded>(acquired);
            Assert.Equal("connect-material", this.derivedMaterial);
            return new CredentialExecutionReady<ParentResult> { Materializer = new Materializer() };
        }

        private sealed class Operation(
            ParentExecutor parent,
            GitHubCredentialAccessExecutor provider,
            List<string> events,
            ICredentialExecutionContext context,
            ProviderAcquisitionKey key) : IProviderAcquisitionOperation
        {
            public ProviderAcquisitionKey CompatibilityKey => key;
            public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
            public long Revision => context.Revision;
            public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
            public Guid AcquisitionGeneration => context.AcquisitionGeneration;

            public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
                ProviderAcquisitionCommitContext commitContext,
                CancellationToken cancellationToken)
            {
                parent.Context = context;
                parent.Permit = commitContext.InteractionPermit;
                if (parent.BodyOverride is { } body)
                {
                    return await body(context, commitContext, cancellationToken);
                }
                var used = parent.UseSplitBoundary
                    ? await this.UseSplitAsync(commitContext, cancellationToken)
                    : await provider.UsePrivateAsync(
                    parent.IdentityRequest,
                    context,
                    commitContext.InteractionPermit,
                    (identity, _) =>
                    {
                        Assert.Equal("identity-material", identity);
                        events.Add("mint");
                        return ValueTask.FromResult("connect-material");
                    },
                    cancellationToken);
                if (used is GitHubPrivateCredentialUseDenied<string>)
                {
                    return new ProviderAcquisitionDenied();
                }
                if (used is GitHubPrivateCredentialUseFailed<string> failed)
                {
                    return new ProviderAcquisitionFailed { ErrorCode = failed.ErrorCode };
                }
                var succeeded = Assert.IsType<GitHubPrivateCredentialUseSucceeded<string>>(used);
                parent.UsedAccount = succeeded.AccountIdentifier;
                parent.UsedGeneration = succeeded.Generation;
                Assert.NotEqual(Guid.Empty, succeeded.Generation.Value);
                return await commitContext.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                {
                    parent.derivedMaterial = succeeded.Result;
                    events.Add("connect committed");
                    return ValueTask.CompletedTask;
                });
            }

            private async ValueTask<GitHubPrivateCredentialUseOutcome<string>> UseSplitAsync(
                ProviderAcquisitionCommitContext parentCommit,
                CancellationToken cancellationToken)
            {
                var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, cancellationToken);
                CredentialMaterialGenerationIdentifier generation;
                if (inspection.Usability == CredentialSourceUsability.Usable)
                {
                    generation = inspection.CachedGeneration!.Generation;
                }
                else
                {
                    var acquisition = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, context);
                    Assert.Equal(inspection.AcquisitionKey, acquisition.CompatibilityKey);
                    var outcome = await context.ExecutePrivateProviderDependencyAsync(acquisition, parentCommit.InteractionPermit, cancellationToken);
                    if (outcome is ProviderAcquisitionFailed failed)
                    {
                        return new GitHubPrivateCredentialUseFailed<string> { ErrorCode = failed.ErrorCode, CanRetry = true };
                    }
                    generation = Assert.IsType<ProviderAcquisitionSucceeded>(outcome).CommittedGeneration;
                }
                if (parent.BeforeGenerationUse is { } beforeUse)
                {
                    await beforeUse(inspection, generation, cancellationToken);
                }
                return await provider.UseGenerationAsync(
                    inspection.Source, generation, context, parentCommit.InteractionPermit, (identity, account, _) =>
                    {
                        Assert.Equal("identity-material", identity);
                        Assert.NotNull(account);
                        events.Add("mint");
                        return ValueTask.FromResult("connect-material");
                    }, cancellationToken);
            }
        }

        private sealed class Materializer : IAccessResultMaterializer<ParentResult>
        {
            public ValueTask<ParentResult> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ParentResult());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Source(List<string> events) : IGitHubCredentialAccessSource
    {
        public int Acquisitions { get; private set; }
        public CredentialAccountIdentifier Account { get; set; } = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "identity-account");
        public string Material { get; set; } = "identity-material";
        public Func<CancellationToken, ValueTask>? BeforeReturn { get; set; }
        public bool EnvironmentExists() => throw new InvalidOperationException("Forced CLI must not inspect another source.");

        public async ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            Assert.Equal(CredentialSourceKind.GitHubCli, source);
            this.Acquisitions++;
            events.Add("identity acquired");
            var result = new GitHubCredentialMaterial
            {
                Material = this.Material.ToCharArray(),
                Account = this.Account,
            };
            if (this.BeforeReturn is { } beforeReturn)
            {
                await beforeReturn(cancellationToken);
            }
            return result;
        }
    }
}

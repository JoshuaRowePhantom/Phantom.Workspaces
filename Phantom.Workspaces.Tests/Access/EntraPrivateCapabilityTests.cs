using System.IO;
using Azure.Core;
using Moq;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class EntraPrivateCapabilityTests
{
    [Fact]
    public async Task EntraPrivateUse_RequiresActiveConsumedConnectCapabilityBeforeWarmMaterial()
    {
        var connect = new ConnectExecutor();
        await using var fixture = new Fixture(connect);
        await using var exported = await fixture.WarmAsync();
        var session = fixture.Entra.GetSession(fixture.Request);
        var generation = (await session.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        var callbackCalls = 0;
        var childExecutions = 0;
        Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync<int>(
            generation, null, null,
            (_, _) => throw new InvalidOperationException("Warm cache is not a private authorization."),
            TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await exported.AccessToken.UseAsync(
            (value, _) => ValueTask.FromResult(value.Length), TestContext.Current.CancellationToken));
        ICredentialExecutionContext? capturedContext = null;
        ProviderInteractionPermit? capturedProof = null;
        connect.Use = async (context, authorization, admission, active, cancellationToken) =>
        {
            capturedContext = context;
            capturedProof = active;
            var key = ConnectExecutor.Key(context.PolicyRequest!);
            var binding = new ProviderExecutionAuthorizationBinding
            {
                Policy = context.Policy,
                Authorization = authorization,
                Request = context.PolicyRequest!,
            };
            Assert.True(active.Authorizes(
                context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration, key));
            Assert.True(active.AuthorizesProvider(CredentialExecutorKind.DevTunnelConnect, key));
            ProviderInteractionPermit Admission(
                CredentialExecutorKind kind,
                ProviderAcquisitionKey candidateKey,
                UserAuthorizationRequestIdentifier? identifier = null,
                long? revision = null,
                Guid? acquisitionGeneration = null) => new(
                    identifier ?? context.RequestIdentifier, revision ?? context.Revision, kind,
                    acquisitionGeneration ?? context.AcquisitionGeneration, candidateKey, () => true, binding);
            ProviderInteractionPermit Bound(
                CredentialExecutorKind kind,
                ProviderAcquisitionKey candidateKey,
                bool isActive = true,
                UserAuthorizationRequestIdentifier? identifier = null,
                long? revision = null,
                Guid? acquisitionGeneration = null) => Admission(
                    kind, candidateKey, identifier, revision, acquisitionGeneration).BindExecution(() => isActive);
            foreach (var denied in new ProviderInteractionPermit?[]
            {
                null,
                admission,
                Admission(CredentialExecutorKind.DevTunnelConnect, key),
                Bound(CredentialExecutorKind.Entra, key),
                Bound(CredentialExecutorKind.DevTunnelConnect, key with { Protocol = ProviderProtocolKind.Sdk }),
                Bound(CredentialExecutorKind.DevTunnelConnect, key with { Configuration = new() { Digest = new string('f', 64) } }),
                Bound(CredentialExecutorKind.DevTunnelConnect, key with { Source = new() { Digest = new string('e', 64) } }),
                Bound(CredentialExecutorKind.DevTunnelConnect, key, isActive: false),
                Bound(CredentialExecutorKind.DevTunnelConnect, key, identifier: new() { Value = Guid.NewGuid() }),
                Bound(CredentialExecutorKind.DevTunnelConnect, key, revision: context.Revision + 1),
                Bound(CredentialExecutorKind.DevTunnelConnect, key, acquisitionGeneration: Guid.NewGuid()),
            })
            {
                Assert.False(denied is not null &&
                    await denied.AuthorizesPrivateUseAsync(context, cancellationToken));
                Assert.IsType<ProviderAcquisitionDenied>(await context.ExecutePrivateProviderDependencyAsync(
                    new ChildOperation(context, key with
                    {
                        Configuration = new() { Digest = new string('a', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, () => childExecutions++),
                    denied!, cancellationToken));
                Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync(
                    generation, context, denied, (_, _) =>
                    {
                        callbackCalls++;
                        return ValueTask.FromResult(0);
                    }, cancellationToken));
            }

            Assert.Equal(0, callbackCalls);
            Assert.Equal(0, childExecutions);
            var unbound = new ProviderInteractionPermit(
                context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration, key, () => true).BindExecution(() => true);
            Assert.False(await unbound.AuthorizesPrivateUseAsync(context, cancellationToken));
            Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync<int>(
                generation, context, unbound,
                (_, _) => throw new InvalidOperationException("An execution shape without a committed binding cannot read material."),
                cancellationToken));
            Assert.IsType<ProviderAcquisitionDenied>(await context.ExecutePrivateProviderDependencyAsync(
                new ChildOperation(context, key with { Protocol = ProviderProtocolKind.Sdk }, () => childExecutions++),
                unbound, cancellationToken));
            var forged = new ProviderInteractionPermit(
                context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration, key, () => true,
                binding with { Authorization = authorization with { Identifier = new() { Value = Guid.NewGuid() } } })
                .BindExecution(() => true);
            Assert.False(await forged.AuthorizesPrivateUseAsync(context, cancellationToken));
            Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync<int>(
                generation, context, forged,
                (_, _) => throw new InvalidOperationException("A forged grant cannot read material."),
                cancellationToken));
            Assert.IsType<ProviderAcquisitionDenied>(await context.ExecutePrivateProviderDependencyAsync(
                new ChildOperation(context, key with { Protocol = ProviderProtocolKind.Sdk }, () => childExecutions++),
                forged, cancellationToken));
            Assert.Equal(0, childExecutions);
            Assert.True(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
            var child = new ChildOperation(context, key with
            {
                Configuration = new() { Digest = new string('a', 64) },
                Protocol = ProviderProtocolKind.Sdk,
            }, () => childExecutions++);
            Assert.Equal(child.Generation, Assert.IsType<ProviderAcquisitionSucceeded>(
                await context.ExecutePrivateProviderDependencyAsync(child, active, cancellationToken)).CommittedGeneration);
            Assert.Equal(1, childExecutions);
            Assert.Null(await session.CopyGenerationAsync(generation, authorization, context, cancellationToken));
            ReadOnlyMemory<char> observed = default;
            var used = Assert.IsType<EntraGenerationUseSucceeded<int>>(await session.UseGenerationAsync(
                generation, context, active, (material, _) =>
                {
                    callbackCalls++;
                    observed = material;
                    Assert.Equal("private-token", new string(material.Span));
                    return ValueTask.FromResult(material.Length);
                }, cancellationToken));
            Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
            return used.Result;
        };
        var pending = fixture.Broker.AcquireAsync(fixture.CreateConnectRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(0, callbackCalls);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last());
        await using var result = Assert.IsType<AccessSucceeded<ConnectResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal("private-token".Length, result.Value);
        Assert.Equal(1, callbackCalls);
        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedProof);
        Assert.False(await capturedProof.AuthorizesPrivateUseAsync(capturedContext, TestContext.Current.CancellationToken));
        Assert.IsType<ProviderAcquisitionDenied>(await capturedContext.ExecutePrivateProviderDependencyAsync(
            new ChildOperation(capturedContext, ConnectExecutor.Key(capturedContext.PolicyRequest!) with
            {
                Protocol = ProviderProtocolKind.Sdk,
            }, () => childExecutions++),
            capturedProof, TestContext.Current.CancellationToken));
        Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync<int>(
            generation, capturedContext, capturedProof,
            (_, _) => throw new InvalidOperationException("A completed generation cannot authorize a late callback."),
            TestContext.Current.CancellationToken));
        Assert.True(await session.IsCurrentGenerationAsync(generation, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Equal(1, childExecutions);
        Assert.Equal(2, fixture.Registry.GetSnapshot().Count);
    }

    [Fact]
    public async Task EntraPrivateUse_QueuedCallbackRechecksConnectProofAfterParentCancellation()
    {
        var connect = new ConnectExecutor();
        await using var fixture = new Fixture(connect);
        await using var exported = await fixture.WarmAsync();
        var session = fixture.Entra.GetSession(fixture.Request);
        var generation = (await session.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ICredentialExecutionContext? parentContext = null;
        ProviderInteractionPermit? parentProof = null;
        connect.Use = async (context, _, _, active, cancellationToken) =>
        {
            parentContext = context;
            parentProof = active;
            var used = await session.UseGenerationAsync(
                generation, context, active, async (material, callbackCancellation) =>
                {
                    using var registration = callbackCancellation.Register(() => stopped.TrySetResult());
                    started.TrySetResult();
                    await finish.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    return material.Length;
                }, cancellationToken);
            return Assert.IsType<EntraGenerationUseSucceeded<int>>(used).Result;
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = fixture.Broker.AcquireAsync(fixture.CreateConnectRequest(), cancellation.Token);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = session.UseGenerationAsync<int>(
            generation, parentContext, parentProof,
            (_, _) => throw new InvalidOperationException("A queued callback must recheck the completed parent capability."),
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(queued.IsCompleted);
        cancellation.Cancel();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        finish.TrySetResult();
        Assert.IsType<AccessCancelled<ConnectResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.IsType<EntraGenerationUseDenied<int>>(
            await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(await session.IsCurrentGenerationAsync(generation, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Acquirer.Calls);
    }

    [Fact]
    public async Task EntraPrivateUse_RevokedGrantDeniesPublicationAndFurtherWarmCallbacks()
    {
        var connect = new ConnectExecutor();
        await using var fixture = new Fixture(connect);
        await using var exported = await fixture.WarmAsync();
        var session = fixture.Entra.GetSession(fixture.Request);
        var generation = (await session.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        var callbackCalls = 0;
        connect.Use = async (context, authorization, _, active, cancellationToken) =>
        {
            ReadOnlyMemory<char> observed = default;
            Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync(
                generation, context, active, async (material, token) =>
                {
                    callbackCalls++;
                    observed = material;
                    Assert.NotNull(authorization.MemoryIdentifier);
                    await context.Policy.ForgetAsync(authorization.MemoryIdentifier, token);
                    return material.Length;
                }, cancellationToken));
            Assert.Equal(1, callbackCalls);
            Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
            Assert.True(active.IsActivePrivateExecution(context));
            Assert.False(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
            Assert.IsType<EntraGenerationUseDenied<int>>(await session.UseGenerationAsync<int>(
                generation, context, active,
                (_, _) => throw new InvalidOperationException("Revoked parent approval cannot read warm identity material."),
                cancellationToken));
            return 0;
        };
        var pending = fixture.Broker.AcquireAsync(fixture.CreateConnectRequest(), TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last(), ReleaseMemoryScopeKind.AllUses);
        Assert.IsType<AccessAuthorizationRequired<ConnectResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(await session.IsCurrentGenerationAsync(generation, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Equal(1, callbackCalls);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await exported.AccessToken.UseAsync(
            (value, _) => ValueTask.FromResult(value.Length), TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Environment.CurrentDirectory, ".entra-private-capability-tests", Guid.NewGuid().ToString("N"));
        internal EntraCredentialAccessRequest Request { get; } = EntraCredentialAccessExecutorTests.CreateRequest();
        internal Acquirer Acquirer { get; } = new();
        internal EntraCredentialAccessExecutor Entra { get; }
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal AccessBroker Broker { get; }

        internal Fixture(ConnectExecutor connect)
        {
            this.Entra = new EntraCredentialAccessExecutor(configuration => new EntraCredentialSession(
                configuration, new Mock<TokenCredential>(MockBehavior.Strict).Object, this.Acquirer));
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.Entra);
            executors.Add<ConnectRequest, ConnectResult>(connect);
            this.Broker = new AccessBroker(
                new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json"))),
                executors, new ProviderAcquisitionCoordinator(), this.Registry);
        }

        internal async Task<EntraCredentialAccessResult> WarmAsync()
        {
            var pending = this.Broker.AcquireAsync(this.Request, TestContext.Current.CancellationToken);
            await this.ApplyAsync(Assert.Single(this.Registry.GetSnapshot()));
            return Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
                await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        }

        internal ConnectRequest CreateConnectRequest() => new()
        {
            Credential = new()
            {
                LogicalIdentifier = new() { Digest = new string('c', 64) },
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                AccountIdentifier = this.Acquirer.Account,
            },
            Requestor = this.Request.Requestor,
            Use = this.Request.Use,
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = [new()
            {
                Identifier = new() { Digest = new string('d', 64) },
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialSourceKind.Provider,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "Connect",
            }],
        };

        internal Task ApplyAsync(
            UserAuthorizationSnapshot snapshot,
            ReleaseMemoryScopeKind? scope = null) => this.Registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = snapshot.Identifier,
            ExpectedRevision = snapshot.Revision,
            Source = snapshot.SelectedSource,
            Memory = scope is null ? snapshot.SelectedMemory : snapshot.MemoryOptions.Single(option => option.Scope == scope).Identifier,
        }, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, true);
            }
        }
    }

    private sealed record ConnectRequest : CredentialAccessRequest<ConnectResult>;

    private sealed class ConnectResult : ICredentialAccessResult
    {
        public required int Value { get; init; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConnectExecutor : ICredentialAccessExecutor<ConnectRequest, ConnectResult>
    {
        private int committedValue;
        internal Func<ICredentialExecutionContext, ReleaseAuthorization, ProviderInteractionPermit,
            ProviderInteractionPermit, CancellationToken, ValueTask<int>>? Use { get; set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelConnect;

        internal static ProviderAcquisitionKey Key(ReleasePolicyRequest request) => new()
        {
            Configuration = request.Credential.LogicalIdentifier,
            Source = request.SourceIdentifier,
            Account = request.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };

        public ValueTask<CredentialInspection> InspectAsync(ConnectRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = request.Sources,
                AcquisitionKeys = request.Sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
                {
                    Configuration = request.Credential.LogicalIdentifier,
                    Source = source.Identifier,
                    Account = request.Credential.AccountIdentifier,
                    Protocol = ProviderProtocolKind.TunnelConnect,
                }),
            });

        public async ValueTask<CredentialExecutionOutcome<ConnectResult>> ExecuteAsync(
            ConnectRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ReleaseAuthorizationValidation.Valid,
                await context.Policy.ValidateAsync(authorization.Identifier, context.PolicyRequest!, cancellationToken));
            Assert.NotNull(interactionPermit);
            var outcome = await context.ExecuteProviderAcquisitionAsync(
                new Operation(this, context, authorization, interactionPermit!), interactionPermit!, cancellationToken);
            if (outcome is not ProviderAcquisitionSucceeded)
            {
                return new CredentialExecutionFailed<ConnectResult>
                {
                    ErrorCode = AccessErrorCode.ProviderFailure,
                    CanRetry = false,
                };
            }
            return new CredentialExecutionReady<ConnectResult> { Materializer = new Materializer(this.committedValue) };
        }

        private sealed class Operation(
            ConnectExecutor executor,
            ICredentialExecutionContext context,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit admission) : IProviderAcquisitionOperation
        {
            public ProviderAcquisitionKey CompatibilityKey => Key(context.PolicyRequest!);
            public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
            public long Revision => context.Revision;
            public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
            public Guid AcquisitionGeneration => context.AcquisitionGeneration;

            public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
                ProviderAcquisitionCommitContext commitContext,
                CancellationToken cancellationToken)
            {
                var value = await executor.Use!(
                    context, authorization, admission, commitContext.InteractionPermit, cancellationToken);
                return await commitContext.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                {
                    executor.committedValue = value;
                    return ValueTask.CompletedTask;
                });
            }
        }

        private sealed class Materializer(int value) : IAccessResultMaterializer<ConnectResult>
        {
            public ValueTask<ConnectResult> MaterializeAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult(new ConnectResult { Value = value });
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Acquirer : IEntraTokenAcquirer
    {
        internal int Calls { get; private set; }
        internal CredentialAccountIdentifier Account { get; } =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "verified-test-principal");

        public ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken)
        {
            this.Calls++;
            return ValueTask.FromResult(new EntraAcquiredToken
            {
                Token = new("private-token", DateTimeOffset.UtcNow.AddHours(1)),
                AccountIdentifier = this.Account,
            });
        }

    }

    private sealed class ChildOperation(
        ICredentialExecutionContext context,
        ProviderAcquisitionKey key,
        Action execute) : IProviderAcquisitionOperation
    {
        internal CredentialMaterialGenerationIdentifier Generation { get; } = new() { Value = Guid.NewGuid() };
        private CredentialMaterialGenerationIdentifier? committedGeneration;
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.Entra;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;

        public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken)
        {
            execute();
            Assert.True(commitContext.InteractionPermit.AuthorizesProvider(this.ExecutorKind, key));
            var outcome = await commitContext.CommitAsync(this.Generation, _ =>
            {
                this.committedGeneration = this.Generation;
                return ValueTask.CompletedTask;
            });
            Assert.Equal(this.Generation, this.committedGeneration);
            return outcome;
        }
    }
}

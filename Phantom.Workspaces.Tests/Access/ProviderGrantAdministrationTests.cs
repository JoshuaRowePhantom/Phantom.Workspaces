using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class ProviderGrantAdministrationTests
{
    private sealed class Facet : IProviderGrantAdministration, IAsyncDisposable
    {
        public IReadOnlyList<ProviderGrantDescriptor> Entries { get; set; } = [];
        public Func<CancellationToken, ValueTask<IReadOnlyList<ProviderGrantDescriptor>>>? Inspect { get; set; }
        public Func<ProviderGrantIdentifier, CancellationToken, ValueTask>? Revoke { get; set; }
        public List<ProviderGrantIdentifier> Revoked { get; } = [];
        public int ListCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<IReadOnlyList<ProviderGrantDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            this.ListCalls++;
            return this.Inspect?.Invoke(cancellationToken) ??
                ValueTask.FromResult<IReadOnlyList<ProviderGrantDescriptor>>(this.Entries.ToArray());
        }

        public ValueTask RevokeAsync(
            ProviderGrantIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            this.Revoked.Add(identifier);
            if (this.Revoke is not null)
            {
                return this.Revoke(identifier, cancellationToken);
            }
            this.Entries = this.Entries.Where(entry => entry.Identifier != identifier).ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private static ProviderGrantDescriptor Descriptor() => new()
    {
        Identifier = new() { Value = Guid.NewGuid() },
        Provider = CredentialProviderKind.McpOAuth,
        Kind = CredentialKind.AccessToken,
        SourceIdentifier = new() { Digest = new string('b', 64) },
    };

    [Fact]
    public async Task List_CollidingBackendIdentifiersRouteOnlyToSelectedProvider()
    {
        var descriptor = Descriptor();
        var first = new Facet { Entries = [descriptor] };
        var second = new Facet { Entries = [descriptor] };
        var aggregate = new ProviderGrantAdministration([first, second]);
        var listed = await aggregate.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, listed.Count);
        Assert.NotEqual(listed[0].Identifier, listed[1].Identifier);
        Assert.All(listed, entry => Assert.NotEqual(descriptor.Identifier, entry.Identifier));
        Assert.All(listed, entry => Assert.Equal(descriptor, entry with { Identifier = descriptor.Identifier }));
        var refreshed = await aggregate.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(listed.Select(entry => entry.Identifier), refreshed.Select(entry => entry.Identifier));
        await aggregate.RevokeAsync(listed[1].Identifier, TestContext.Current.CancellationToken);
        Assert.Empty(first.Revoked);
        Assert.Equal(descriptor.Identifier, Assert.Single(second.Revoked));
        Assert.Single(first.Entries);
        Assert.Empty(second.Entries);
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.RevokeAsync(listed[1].Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
    }

    [Fact]
    public async Task Revoke_DisappearedOrChangedGrantNeverMapsToReplacement()
    {
        var descriptor = Descriptor();
        var facet = new Facet { Entries = [descriptor] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var original = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        facet.Entries = [descriptor with { SourceIdentifier = new() { Digest = new string('c', 64) } }];
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.RevokeAsync(original.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
        Assert.Empty(facet.Revoked);
        var replacement = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(original.Identifier, replacement.Identifier);
        facet.Entries = [];
        Assert.Empty(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        facet.Entries = [descriptor];
        var reappeared = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(original.Identifier, reappeared.Identifier);
        await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.RevokeAsync(original.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(facet.Revoked);
    }

    [Fact]
    public async Task List_ConcurrentRefreshesSerializeAndKeepStableOpaqueIdentifiers()
    {
        var descriptor = Descriptor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<ProviderGrantDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facet = new Facet();
        facet.Inspect = _ =>
        {
            if (facet.ListCalls == 1)
            {
                entered.SetResult();
                return new(completion.Task);
            }
            return ValueTask.FromResult<IReadOnlyList<ProviderGrantDescriptor>>([descriptor]);
        };
        var aggregate = new ProviderGrantAdministration([facet]);
        var first = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(1, facet.ListCalls);
        completion.SetResult([descriptor]);
        Assert.Equal(Assert.Single(await first).Identifier, Assert.Single(await second).Identifier);
    }

    [Fact]
    public async Task Revoke_SerializesRefreshAndDelegatesRemoteRevocationAndCacheInvalidation()
    {
        var descriptor = Descriptor();
        var facet = new Facet { Entries = [descriptor] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();
        facet.Revoke = async (identifier, cancellationToken) =>
        {
            Assert.Equal(descriptor.Identifier, identifier);
            operations.Add("remote revocation");
            entered.SetResult();
            await completion.Task.WaitAsync(cancellationToken);
            operations.Add("cache invalidation");
            facet.Entries = [];
        };
        var revoking = aggregate.RevokeAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var refresh = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(refresh.IsCompleted);
        Assert.Equal(2, facet.ListCalls);
        completion.SetResult();
        await revoking;
        Assert.Empty(await refresh);
        Assert.Equal(new[] { "remote revocation", "cache invalidation" }, operations);
        Assert.Single(facet.Revoked);
    }

    [Fact]
    public async Task List_DuplicateBackendIdentifiersFailWithoutReplacingPreviousBindings()
    {
        var descriptor = Descriptor();
        var facet = new Facet { Entries = [descriptor] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        facet.Entries = [descriptor, descriptor];
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Failed, failure.Failure);
        await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.RevokeAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(facet.Revoked);
        facet.Entries = [descriptor];
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Theory]
    [InlineData(false, CredentialAdministrationFailure.Unavailable)]
    [InlineData(false, CredentialAdministrationFailure.Denied)]
    [InlineData(true, CredentialAdministrationFailure.NoLongerCurrent)]
    [InlineData(true, CredentialAdministrationFailure.Failed)]
    public async Task KnownFailuresPropagateUnchangedAndLeaveGateUsable(
        bool duringRevoke,
        CredentialAdministrationFailure reason)
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var failure = new CredentialAdministrationException(reason);
        if (duringRevoke) { facet.Revoke = (_, _) => throw failure; }
        else { facet.Inspect = _ => throw failure; }
        var actual = await Assert.ThrowsAsync<CredentialAdministrationException>(() => duringRevoke
            ? aggregate.RevokeAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask()
            : aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, actual);
        facet.Revoke = null;
        facet.Inspect = null;
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownImplementationBugsPropagateRatherThanBecomingEmptyCatalogs(
        bool duringRevoke)
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var failure = new InvalidOperationException("Unexpected implementation bug.");
        if (duringRevoke) { facet.Revoke = (_, _) => throw failure; }
        else { facet.Inspect = _ => throw failure; }
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => duringRevoke
            ? aggregate.RevokeAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask()
            : aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ProviderCancellationPropagatesAndPreservesSelectedBinding()
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new ProviderGrantAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        facet.Revoke = (_, cancellationToken) =>
        {
            Assert.Equal(cancellation.Token, cancellationToken);
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellationToken);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            aggregate.RevokeAsync(selected.Identifier, cancellation.Token).AsTask());
        facet.Revoke = null;
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Fact]
    public async Task QueuedRefreshCancellationDoesNotQueryProviders()
    {
        var descriptor = Descriptor();
        var completion = new TaskCompletionSource<IReadOnlyList<ProviderGrantDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facet = new Facet { Inspect = _ => new(completion.Task) };
        var aggregate = new ProviderGrantAdministration([facet]);
        var blocked = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = aggregate.ListAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, facet.ListCalls);
        completion.SetResult([descriptor]);
        Assert.Single(await blocked);
    }

    [Fact]
    public async Task EmptySupportedGrantCatalogIsLegitimateAndDoesNotInventLogoutRevocation()
    {
        var facet = new Facet();
        IProviderGrantAdministration[] supplied = [facet];
        var aggregate = new ProviderGrantAdministration(supplied);
        supplied[0] = new Facet { Entries = [Descriptor()] };
        Assert.Equal(0, facet.ListCalls);
        Assert.Empty(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await new ProviderGrantAdministration([]).ListAsync(TestContext.Current.CancellationToken));
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.RevokeAsync(new() { Value = Guid.NewGuid() }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
        Assert.Empty(facet.Revoked);
        Assert.Equal(0, facet.DisposeCalls);
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(aggregate.GetType()));
        Assert.Throws<ArgumentException>(() => new ProviderGrantAdministration([facet, facet]));
        Assert.Throws<ArgumentNullException>(() => new ProviderGrantAdministration(null!));
    }
}

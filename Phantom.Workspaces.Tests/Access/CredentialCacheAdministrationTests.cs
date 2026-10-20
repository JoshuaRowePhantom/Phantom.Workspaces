using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class CredentialCacheAdministrationTests
{
    private sealed class Facet : ICredentialCacheAdministration, IAsyncDisposable
    {
        public IReadOnlyList<CredentialCacheDescriptor> Entries { get; set; } = [];
        public Func<CancellationToken, ValueTask<IReadOnlyList<CredentialCacheDescriptor>>>? Inspect { get; set; }
        public Func<CredentialCacheIdentifier, CancellationToken, ValueTask>? Delete { get; set; }
        public List<CredentialCacheIdentifier> Deleted { get; } = [];
        public int ListCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            this.ListCalls++;
            return this.Inspect?.Invoke(cancellationToken) ??
                ValueTask.FromResult<IReadOnlyList<CredentialCacheDescriptor>>(this.Entries.ToArray());
        }

        public ValueTask DeleteAsync(
            CredentialCacheIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            this.Deleted.Add(identifier);
            if (this.Delete is not null)
            {
                return this.Delete(identifier, cancellationToken);
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

    private static CredentialCacheDescriptor Descriptor() => new()
    {
        Identifier = new() { Value = Guid.NewGuid() },
        Provider = CredentialProviderKind.Entra,
        Kind = CredentialKind.AccessToken,
        Generation = new() { Value = Guid.NewGuid() },
        SourceIdentifier = new() { Digest = new string('a', 64) },
        ExpiresOn = DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
    };

    [Fact]
    public async Task List_CollidingBackendIdentifiersStayStableAndRouteToExactFacet()
    {
        var descriptor = Descriptor();
        var first = new Facet { Entries = [descriptor] };
        var second = new Facet { Entries = [descriptor] };
        var aggregate = new CredentialCacheAdministration([first, second]);
        var listed = await aggregate.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, listed.Count);
        Assert.NotEqual(listed[0].Identifier, listed[1].Identifier);
        Assert.All(listed, entry => Assert.NotEqual(descriptor.Identifier, entry.Identifier));
        Assert.All(listed, entry => Assert.Equal(descriptor, entry with { Identifier = descriptor.Identifier }));
        var refreshed = await aggregate.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(listed.Select(entry => entry.Identifier), refreshed.Select(entry => entry.Identifier));
        await aggregate.DeleteAsync(listed[1].Identifier, TestContext.Current.CancellationToken);
        Assert.Empty(first.Deleted);
        Assert.Equal(descriptor.Identifier, Assert.Single(second.Deleted));
        Assert.Single(first.Entries);
        Assert.Empty(second.Entries);
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.DeleteAsync(listed[1].Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
    }

    [Fact]
    public async Task Delete_RotatedBackendIdentifierCannotRebindOldSelection()
    {
        var original = Descriptor();
        var facet = new Facet { Entries = [original] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        facet.Entries = [original with { Generation = new() { Value = Guid.NewGuid() } }];
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
        Assert.Empty(facet.Deleted);
        var replacement = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(selected.Identifier, replacement.Identifier);
        await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(facet.Deleted);
        await aggregate.DeleteAsync(replacement.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(original.Identifier, Assert.Single(facet.Deleted));
    }

    [Fact]
    public async Task List_ConcurrentRefreshesCannotPublishAnOlderCatalogLast()
    {
        var original = Descriptor();
        var replacement = original with { Generation = new() { Value = Guid.NewGuid() } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<CredentialCacheDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facet = new Facet();
        facet.Inspect = _ =>
        {
            if (facet.ListCalls == 1)
            {
                entered.SetResult();
                return new(completion.Task);
            }
            return ValueTask.FromResult<IReadOnlyList<CredentialCacheDescriptor>>([replacement]);
        };
        var aggregate = new CredentialCacheAdministration([facet]);
        var first = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(1, facet.ListCalls);
        completion.SetResult([original]);
        var old = Assert.Single(await first);
        var current = Assert.Single(await second);
        Assert.NotEqual(old.Identifier, current.Identifier);
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.DeleteAsync(old.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
        Assert.Empty(facet.Deleted);
    }

    [Fact]
    public async Task Delete_SerializesRefreshUntilSelectedBackendActionCompletes()
    {
        var descriptor = Descriptor();
        var facet = new Facet { Entries = [descriptor] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        facet.Delete = async (_, cancellationToken) =>
        {
            entered.SetResult();
            await completion.Task.WaitAsync(cancellationToken);
            facet.Entries = [];
        };
        var deleting = aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var refresh = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(refresh.IsCompleted);
        Assert.Equal(2, facet.ListCalls);
        completion.SetResult();
        await deleting;
        Assert.Empty(await refresh);
        Assert.Equal(descriptor.Identifier, Assert.Single(facet.Deleted));
    }

    [Fact]
    public async Task List_CancelledQueuedRefreshDoesNotInspectOrReplaceBindings()
    {
        var descriptor = Descriptor();
        var facet = new Facet { Entries = [descriptor] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var completion = new TaskCompletionSource<IReadOnlyList<CredentialCacheDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        facet.Inspect = _ => new(completion.Task);
        var blocked = aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = aggregate.ListAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(2, facet.ListCalls);
        completion.SetResult([descriptor]);
        Assert.Equal(selected.Identifier, Assert.Single(await blocked).Identifier);
        facet.Inspect = null;
        await aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task List_DuplicateIdentifiersWithinFacetFailWithoutPublishingPartialCatalog()
    {
        var original = Descriptor();
        var facet = new Facet { Entries = [original] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        facet.Entries = [original, original with { Generation = new() { Value = Guid.NewGuid() } }];
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Failed, failure.Failure);
        await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(facet.Deleted);
        facet.Entries = [original];
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Theory]
    [InlineData(false, CredentialAdministrationFailure.Unavailable)]
    [InlineData(false, CredentialAdministrationFailure.Denied)]
    [InlineData(true, CredentialAdministrationFailure.NoLongerCurrent)]
    [InlineData(true, CredentialAdministrationFailure.Failed)]
    public async Task KnownFailuresPropagateUnchangedAndReleaseSerializationGate(
        bool duringDelete,
        CredentialAdministrationFailure reason)
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var failure = new CredentialAdministrationException(reason);
        if (duringDelete) { facet.Delete = (_, _) => throw failure; }
        else { facet.Inspect = _ => throw failure; }
        var actual = await Assert.ThrowsAsync<CredentialAdministrationException>(() => duringDelete
            ? aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask()
            : aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, actual);
        facet.Delete = null;
        facet.Inspect = null;
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownImplementationBugsAreNotSwallowed(
        bool duringDelete)
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        var failure = new InvalidOperationException("Unexpected implementation bug.");
        if (duringDelete) { facet.Delete = (_, _) => throw failure; }
        else { facet.Inspect = _ => throw failure; }
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => duringDelete
            ? aggregate.DeleteAsync(selected.Identifier, TestContext.Current.CancellationToken).AsTask()
            : aggregate.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ProviderActionCancellationPropagatesWithoutDroppingOriginalBinding()
    {
        var facet = new Facet { Entries = [Descriptor()] };
        var aggregate = new CredentialCacheAdministration([facet]);
        var selected = Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        facet.Delete = (_, cancellationToken) =>
        {
            Assert.Equal(cancellation.Token, cancellationToken);
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellationToken);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            aggregate.DeleteAsync(selected.Identifier, cancellation.Token).AsTask());
        facet.Delete = null;
        Assert.Equal(selected.Identifier,
            Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken)).Identifier);
    }

    [Fact]
    public async Task CompositionCopiesRealFacetsWithoutTakingOwnershipOrInventingEntries()
    {
        var facet = new Facet { Entries = [Descriptor()] };
        ICredentialCacheAdministration[] supplied = [facet];
        var aggregate = new CredentialCacheAdministration(supplied);
        supplied[0] = new Facet();
        Assert.Equal(0, facet.ListCalls);
        Assert.Single(await aggregate.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, facet.DisposeCalls);
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(aggregate.GetType()));
        Assert.Throws<ArgumentException>(() => new CredentialCacheAdministration([facet, facet]));
        Assert.Throws<ArgumentNullException>(() => new CredentialCacheAdministration(null!));
        Assert.Empty(await new CredentialCacheAdministration([]).ListAsync(TestContext.Current.CancellationToken));
    }
}

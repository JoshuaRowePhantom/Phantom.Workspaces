using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class UserAuthorizationRegistryTests
{
    [Fact]
    public async Task UserAuthorizationRegistry_CallerCancellationClosesLatestRevisionExactlyOnce()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        Assert.True(registry.UpdateSources(snapshot.Identifier, snapshot.Revision,
            [snapshot.SourceOptions[0] with { Usability = CredentialSourceUsability.Usable }]));
        var beforeCancellation = Assert.Single(registry.GetSnapshot());
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        Assert.True(registry.Cancel(snapshot.Identifier));
        var cancelled = Assert.Single(registry.GetSnapshot());
        Assert.Equal(beforeCancellation.Revision + 1, cancelled.Revision);
        Assert.Equal(UserAuthorizationState.Cancelled, cancelled.State);
        Assert.False(cancelled.CanAct);
        Assert.False(registry.Cancel(snapshot.Identifier));
        Assert.Same(cancelled, Assert.Single(registry.GetSnapshot()));
        Assert.Equal(1, changes);
        Assert.Equal(UserAuthorizationState.Cancelled,
            (await registry.WaitForCompletionAsync(snapshot.Identifier, TestContext.Current.CancellationToken)).State);
    }

    [Theory]
    [InlineData(UserAuthorizationState.Completed)]
    [InlineData(UserAuthorizationState.Denied)]
    public async Task UserAuthorizationRegistry_CallerCancellationPreservesTerminalDecision(UserAuthorizationState state)
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        Assert.True(registry.Complete(snapshot.Identifier, snapshot.Revision, state));
        var completed = Assert.Single(registry.GetSnapshot());

        Assert.False(registry.Cancel(snapshot.Identifier));
        Assert.Same(completed, Assert.Single(registry.GetSnapshot()));
    }

    [Fact]
    public async Task UserAuthorizationRegistry_ActAsync_StaleRevision_HasNoSideEffects()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        var action = new ApplyUserAuthorizationAction
        {
            RequestIdentifier = snapshot.Identifier,
            ExpectedRevision = snapshot.Revision,
            Memory = snapshot.SelectedMemory,
            Source = snapshot.SelectedSource,
        };
        await registry.ActAsync(action, TestContext.Current.CancellationToken);
        var changed = Assert.Single(registry.GetSnapshot());
        var exception = await Assert.ThrowsAsync<UserAuthorizationActionRejectedException>(() =>
            registry.ActAsync(action, TestContext.Current.CancellationToken));
        Assert.Equal(UserAuthorizationActionRejection.StaleRevision, exception.Reason);
        Assert.Same(changed, Assert.Single(registry.GetSnapshot()));
        var decision = await registry.WaitForDecisionAsync(snapshot.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(changed.Revision, decision.Revision);
        Assert.True(registry.Complete(snapshot.Identifier, decision.Revision, UserAuthorizationState.Completed));
        Assert.False(registry.Complete(snapshot.Identifier, decision.Revision, UserAuthorizationState.Failed));
        Assert.Equal(UserAuthorizationState.Completed,
            (await registry.WaitForCompletionAsync(snapshot.Identifier, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task UserAuthorizationRegistry_SourceBecomesUsable_ChangesPerformToAllowInPlace()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        Assert.True(registry.UpdateSources(snapshot.Identifier, snapshot.Revision,
            new[] { snapshot.SourceOptions[0] with { Usability = CredentialSourceUsability.Usable } }));
        var updated = Assert.Single(registry.GetSnapshot());
        Assert.Equal(snapshot.Identifier, updated.Identifier);
        Assert.Equal(snapshot.SelectedMemory, updated.SelectedMemory);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, updated.PrimaryAction);
        Assert.Equal(snapshot.Revision + 1, updated.Revision);
    }

    [Fact]
    public async Task UserAuthorizationRegistry_GetSnapshot_OmitsSensitiveAndExecutionData()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        var allowed = new[]
        {
            "Identifier", "Revision", "UseDisplayName", "State", "PrimaryAction", "MemoryOptions", "SelectedMemory",
            "SourceOptions", "SelectedSource", "SafeStatus", "IsBootstrapBlocking", "CanAct",
        };
        Assert.Equal(allowed.Order(), typeof(UserAuthorizationSnapshot).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(typeof(UserAuthorizationSnapshot).GetProperties(),
            property => typeof(Delegate).IsAssignableFrom(property.PropertyType) || typeof(IAccessResult).IsAssignableFrom(property.PropertyType));
        Assert.Throws<NotSupportedException>(() => ((IList<CredentialSourceChoiceDescriptor>)Assert.Single(registry.GetSnapshot()).SourceOptions).Clear());
        var completion = registry.WaitForCompletionAsync(snapshot.Identifier, TestContext.Current.CancellationToken);
        var clearedObserved = false;
        registry.Changed += (_, _) => clearedObserved |= registry.GetSnapshot().Count == 0;
        await registry.DisposeAsync();
        Assert.Equal(UserAuthorizationState.Cancelled, (await completion).State);
        Assert.Empty(registry.GetSnapshot());
        Assert.True(clearedObserved);
    }

    [Fact]
    public async Task UserAuthorizationRegistry_ConcurrentActions_OnlyOneRevisionCanWin()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        async Task<bool> ApplyAsync()
        {
            try
            {
                await Task.Yield();
                await registry.ActAsync(new ApplyUserAuthorizationAction
                {
                    RequestIdentifier = snapshot.Identifier,
                    ExpectedRevision = snapshot.Revision,
                    Memory = snapshot.SelectedMemory,
                    Source = snapshot.SelectedSource,
                }, TestContext.Current.CancellationToken);
                return true;
            }
            catch (UserAuthorizationActionRejectedException exception) when (exception.Reason == UserAuthorizationActionRejection.StaleRevision)
            {
                return false;
            }
        }

        Assert.Single(await Task.WhenAll(ApplyAsync(), ApplyAsync()), accepted => accepted);
        Assert.Equal(2, Assert.Single(registry.GetSnapshot()).Revision);
    }

    [Fact]
    public async Task UserAuthorizationRegistry_WaitForCompletionAsync_TerminalStatesCompleteButRenewalStillWaits()
    {
        await using var registry = new UserAuthorizationRegistry();
        foreach (var state in new[]
        {
            UserAuthorizationState.Completed, UserAuthorizationState.Failed,
            UserAuthorizationState.Denied, UserAuthorizationState.Cancelled,
        })
        {
            var snapshot = Snapshot();
            registry.Add(snapshot);
            var wait = registry.WaitForCompletionAsync(snapshot.Identifier, TestContext.Current.CancellationToken);
            Assert.True(registry.Complete(snapshot.Identifier, snapshot.Revision, state));
            Assert.Equal(state, (await wait).State);
        }

        var renewal = Snapshot();
        registry.Add(renewal);
        var renewalWait = registry.WaitForCompletionAsync(renewal.Identifier, TestContext.Current.CancellationToken);
        Assert.True(registry.Complete(renewal.Identifier, renewal.Revision, UserAuthorizationState.RenewalNeeded));
        Assert.False(renewalWait.IsCompleted);
        var updated = Assert.Single(registry.GetSnapshot(), snapshot => snapshot.Identifier == renewal.Identifier);
        Assert.True(registry.Complete(renewal.Identifier, updated.Revision, UserAuthorizationState.Completed));
        Assert.Equal(UserAuthorizationState.Completed, (await renewalWait).State);
    }

    internal static UserAuthorizationSnapshot Snapshot()
    {
        var memory = new ReleaseMemoryOptionDescriptor
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Scope = ReleaseMemoryScopeKind.AllowOnce,
            SafeDisplayName = "Once",
        };
        var source = new CredentialSourceChoiceDescriptor
        {
            Identifier = new() { Digest = new string('b', 64) },
            Kind = CredentialSourceKind.Provider,
            Provider = CredentialProviderKind.OAuth,
            Usability = CredentialSourceUsability.AcquisitionRequired,
            SafeDisplayName = "OAuth",
        };
        return new()
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Revision = 1,
            UseDisplayName = "Authenticate",
            State = UserAuthorizationState.Pending,
            PrimaryAction = UserAuthorizationPrimaryActionKind.Perform,
            MemoryOptions = new[] { memory },
            SelectedMemory = memory.Identifier,
            SourceOptions = new[] { source },
            SelectedSource = source.Identifier,
            SafeStatus = "Authorization required.",
            IsBootstrapBlocking = false,
            CanAct = true,
        };
    }
}

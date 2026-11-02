using System.Collections.Generic;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class AuthorizationRequestRowsViewModelTests
{
    internal sealed class Registry : IUserAuthorizationRegistry
    {
        public event EventHandler<UserAuthorizationChangedEventArgs>? Changed;
        public IReadOnlyList<UserAuthorizationSnapshot> Snapshot { get; set; } = [];
        public UserAuthorizationAction? Action { get; private set; }
        public Func<UserAuthorizationAction, Task>? ExecuteAction { get; set; }
        public int Subscribers => this.Changed?.GetInvocationList().Length ?? 0;
        public Action? DuringSnapshot { get; set; }
        public IReadOnlyList<UserAuthorizationSnapshot> GetSnapshot()
        {
            var callback = this.DuringSnapshot;
            this.DuringSnapshot = null;
            callback?.Invoke();
            return this.Snapshot;
        }
        public void Notify() => this.Changed?.Invoke(this, new()
        {
            Identifier = this.Snapshot.FirstOrDefault()?.Identifier ?? AuthorizationRequestRowsViewModelTests.Snapshot().Identifier,
            Revision = this.Snapshot.FirstOrDefault()?.Revision ?? 0,
        });
        public Task ActAsync(UserAuthorizationAction action, CancellationToken cancellationToken = default)
        {
            this.Action = action;
            return this.ExecuteAction?.Invoke(action) ?? Task.CompletedTask;
        }
        public Task<UserAuthorizationCompletion> WaitForCompletionAsync(UserAuthorizationRequestIdentifier identifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static UserAuthorizationSnapshot Snapshot(long revision = 1) => new()
    {
        Identifier = new() { Value = Guid.Parse("47e37147-a07c-43a2-a719-dd6d6b7a1861") },
        Revision = revision,
        UseDisplayName = "Application credential",
        State = UserAuthorizationState.Pending,
        PrimaryAction = UserAuthorizationPrimaryActionKind.Allow,
        MemoryOptions = [new()
        {
            Identifier = new() { Value = Guid.Empty },
            Scope = ReleaseMemoryScopeKind.AllowOnce,
            SafeDisplayName = "Once",
        }],
        SelectedMemory = new() { Value = Guid.Empty },
        SourceOptions = [new()
        {
            Identifier = new() { Digest = new string('a', 64) },
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialSourceKind.ProviderCache,
            Usability = CredentialSourceUsability.Usable,
            SafeDisplayName = "Provider cache",
        }],
        SelectedSource = new() { Digest = new string('a', 64) },
        SafeStatus = "Authorization required.",
        IsBootstrapBlocking = true,
        CanAct = true,
    };

    [Fact]
    public async Task ActionableCount_LocalSubmissionAndRejection_NotifiesWithoutRegistryRevision()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new Registry { Snapshot = [Snapshot()], ExecuteAction = _ => completion.Task };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var counts = new List<int>();
        rows.Changed += (_, _) => counts.Add(rows.ActionableCount);
        var action = Assert.Single(rows.Rows).ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, rows.ActionableCount);
        completion.SetException(new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.StaleRevision));
        await action;
        Assert.Equal(1, rows.ActionableCount);
        Assert.Contains(0, counts);
        Assert.Equal(1, counts[^1]);
    }

    [Fact]
    public async Task PromptGeneration_SourceRefreshAndCoalescedRenewal_PresentsOnlyFreshPrompts()
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        var callbacks = new List<Action>();
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = callbacks.Add, Registry = registry };
        callbacks[0]();
        var prompts = 0;
        rows.NewActionablePrompt += (_, arguments) => { prompts++; arguments.Presentation = Task.FromResult(true); };
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Equal(1, prompts);
        Assert.True(registry.UpdateSources(snapshot.Identifier, 1, snapshot.SourceOptions));
        callbacks[^1]();
        Assert.Equal(1, prompts);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = snapshot.Identifier, ExpectedRevision = 2,
            Memory = snapshot.SelectedMemory, Source = snapshot.SelectedSource,
        }, TestContext.Current.CancellationToken);
        Assert.True(registry.Complete(snapshot.Identifier, 3, UserAuthorizationState.RenewalNeeded));
        callbacks[^1]();
        callbacks[^2]();
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Equal(2, prompts);
        Assert.Equal(2, Assert.Single(registry.GetSnapshot()).PromptGeneration);
    }

    [Theory]
    [InlineData(UserAuthorizationState.Pending, true, 1)]
    [InlineData(UserAuthorizationState.Failed, true, 1)]
    [InlineData(UserAuthorizationState.RenewalNeeded, true, 1)]
    [InlineData(UserAuthorizationState.Pending, false, 0)]
    [InlineData(UserAuthorizationState.Performing, true, 0)]
    [InlineData(UserAuthorizationState.Completed, true, 0)]
    [InlineData(UserAuthorizationState.Denied, true, 0)]
    [InlineData(UserAuthorizationState.Cancelled, true, 0)]
    public void ActionableCount_StatesAndRemoval_UsesUniqueLiveRows(UserAuthorizationState state, bool canAct, int count)
    {
        var registry = new Registry { Snapshot = [Snapshot() with { State = state, CanAct = canAct }] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var prompts = 0;
        rows.NewActionablePrompt += (_, arguments) => { prompts++; arguments.Presentation = Task.FromResult(true); };
        Assert.Equal(0, prompts);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        registry.Notify();
        Assert.Equal(count, rows.ActionableCount);
        Assert.Equal(count, prompts);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Bootstrap);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Equal(count, prompts);
        registry.Snapshot = [];
        registry.Notify();
        Assert.Equal(0, rows.ActionableCount);
        Assert.Empty(rows.Rows);
        Assert.Equal(count, prompts);
    }

    [Fact]
    public async Task ActionableCount_MixedUniqueRows_RemainsIndependentOfBootstrapFilterAndRevisions()
    {
        var states = new[]
        {
            UserAuthorizationState.Pending, UserAuthorizationState.Failed, UserAuthorizationState.RenewalNeeded,
            UserAuthorizationState.Performing, UserAuthorizationState.Completed, UserAuthorizationState.Denied,
            UserAuthorizationState.Cancelled, UserAuthorizationState.Pending,
        };
        var snapshots = states.Select((state, index) => Snapshot() with
        {
            Identifier = new() { Value = Guid.NewGuid() },
            State = state,
            CanAct = index != 7,
            IsBootstrapBlocking = index != 2,
        }).ToArray();
        var registry = new Registry { Snapshot = snapshots };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        Assert.Equal(7, rows.Rows.Count);
        Assert.Equal(3, rows.ActionableCount);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ExecuteAction = _ => completion.Task;
        var submitting = rows.Rows[0].ApplyCommand.ExecuteAsync(null);
        Assert.Equal(2, rows.ActionableCount);
        completion.SetException(new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.StaleRevision));
        await submitting;
        Assert.Equal(3, rows.ActionableCount);
        registry.Snapshot = snapshots.Select(snapshot => snapshot with { Revision = 2 }).ToArray();
        registry.Notify();
        registry.Notify();
        Assert.Equal(3, rows.ActionableCount);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Equal(8, rows.Rows.Count);
        Assert.Equal(3, rows.ActionableCount);
        var removed = rows.Rows[1];
        registry.Snapshot = registry.Snapshot.Where(snapshot => snapshot.Identifier != removed.Identifier).ToArray();
        registry.Notify();
        Assert.False(removed.CanAct);
        Assert.Equal(2, rows.ActionableCount);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Bootstrap);
        Assert.Equal(1, rows.Rows.Count(row => row.CanAct));
        Assert.Equal(2, rows.ActionableCount);
    }

    [Theory]
    [InlineData(UserAuthorizationState.Failed)]
    [InlineData(UserAuthorizationState.RenewalNeeded)]
    public async Task PromptGeneration_RepeatedCompletionSourcesAndRetry_OnlyNewAttemptsPresent(
        UserAuthorizationState completionState)
    {
        await using var registry = new UserAuthorizationRegistry();
        var snapshot = Snapshot();
        registry.Add(snapshot);
        var callbacks = new List<Action>();
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = callbacks.Add, Registry = registry };
        callbacks[0]();
        var prompts = 0;
        rows.NewActionablePrompt += (_, arguments) => { prompts++; arguments.Presentation = Task.FromResult(true); };
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Equal(1, Assert.Single(registry.GetSnapshot()).PromptGeneration);
        Assert.Equal(1, prompts);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = Assert.Single(registry.GetSnapshot());
            if (attempt == 0)
            {
                await registry.ActAsync(new ApplyUserAuthorizationAction
                {
                    RequestIdentifier = snapshot.Identifier, ExpectedRevision = before.Revision,
                    Source = snapshot.SelectedSource, Memory = snapshot.SelectedMemory,
                }, TestContext.Current.CancellationToken);
            }
            else
            {
                await registry.ActAsync(new RetryUserAuthorizationAction
                {
                    RequestIdentifier = snapshot.Identifier, ExpectedRevision = before.Revision,
                }, TestContext.Current.CancellationToken);
            }
            var performing = Assert.Single(registry.GetSnapshot());
            Assert.Equal(before.PromptGeneration, performing.PromptGeneration);
            Assert.True(registry.Complete(snapshot.Identifier, performing.Revision, completionState));
            var completed = Assert.Single(registry.GetSnapshot());
            Assert.Equal(attempt + 2, completed.PromptGeneration);
            callbacks[^1]();
            callbacks[^2]();
            Assert.Equal(attempt + 2, prompts);
            Assert.True(registry.Complete(snapshot.Identifier, completed.Revision, completionState));
            var repeated = Assert.Single(registry.GetSnapshot());
            Assert.Equal(completed.PromptGeneration, repeated.PromptGeneration);
            Assert.True(registry.UpdateSources(snapshot.Identifier, repeated.Revision, snapshot.SourceOptions));
            callbacks[^1]();
            callbacks[^2]();
            rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
            Assert.Equal(completed.PromptGeneration, Assert.Single(registry.GetSnapshot()).PromptGeneration);
            Assert.Equal(attempt + 2, prompts);
        }
    }

    [Fact]
    public async Task Presentation_InFlightRevisionsAndFailure_OnlySuccessfulDeliveryAcknowledgesGeneration()
    {
        var registry = new Registry { Snapshot = [Snapshot() with { PromptGeneration = 1 }] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        rows.NewActionablePrompt += (_, arguments) =>
        {
            prompts++;
            arguments.Presentation = completion.Task;
        };
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        registry.Snapshot = [registry.Snapshot[0] with { Revision = 2 }];
        registry.Notify();
        rows.PresentFreshPrompts();
        Assert.Equal(1, prompts);
        completion.SetResult(false);
        await rows.PresentationReady;
        completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rows.PresentFreshPrompts();
        Assert.Equal(2, prompts);
        var failed = completion;
        completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rows.NotifyPresentationOwnerAvailabilityChanged();
        failed.SetResult(false);
        await rows.PresentationReady;
        Assert.Equal(3, prompts);
        completion.SetResult(true);
        await rows.PresentationReady;
        registry.Snapshot = [registry.Snapshot[0] with { Revision = 3 }];
        registry.Notify();
        rows.PresentFreshPrompts();
        Assert.Equal(3, prompts);
    }

    [Fact]
    public void SubscribesBeforeInitialSnapshotAndDiscardsOutOfOrderQueuedSnapshots()
    {
        var callbacks = new List<Action>();
        var registry = new Registry { Snapshot = [Snapshot()] };
        registry.DuringSnapshot = () => Assert.Equal(1, registry.Subscribers);
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = callbacks.Add, Registry = registry };
        registry.Snapshot = [Snapshot(3)];
        registry.Notify();
        callbacks[1]();
        var row = Assert.Single(rows.Rows);
        callbacks[0]();
        Assert.Same(row, Assert.Single(rows.Rows));
        Assert.Equal(3, row.Revision);
    }

    [Fact]
    public async Task PreservesIdentityDetailsAndSubmitsExactRevisionAndSelection()
    {
        var registry = new Registry { Snapshot = [Snapshot()] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var row = Assert.Single(rows.Rows);
        row.IsDetailsExpanded = true;
        registry.Snapshot = [Snapshot(2)];
        registry.Notify();
        Assert.Same(row, Assert.Single(rows.Rows));
        Assert.True(row.IsDetailsExpanded);
        await row.ApplyCommand.ExecuteAsync(null);
        var action = Assert.IsType<ApplyUserAuthorizationAction>(registry.Action);
        Assert.Equal(2, action.ExpectedRevision);
        Assert.Equal(row.Identifier, action.RequestIdentifier);
        Assert.Equal(row.SelectedSource, action.Source);
        registry.Snapshot = [Snapshot(3) with { State = UserAuthorizationState.Performing, CanAct = false }];
        registry.Notify();
        Assert.False(row.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void FiltersWithoutReplacingRowsAndDisposesQueuedCallbacks()
    {
        var registry = new Registry { Snapshot = [Snapshot() with { IsBootstrapBlocking = false }] };
        var callbacks = new List<Action>();
        var rows = new AuthorizationRequestRowsViewModel { Dispatch = callbacks.Add, Registry = registry };
        callbacks[0]();
        Assert.Empty(rows.Rows);
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        var row = Assert.Single(rows.Rows);
        registry.Snapshot = [Snapshot(2)];
        registry.Notify();
        rows.Dispose();
        Assert.Equal(0, registry.Subscribers);
        callbacks[1]();
        callbacks[2]();
        Assert.Empty(rows.Rows);
        Assert.False(row.CanAct);
    }

    [Fact]
    public async Task PreservesValidSelectionsAndRejectsLowerRevisionSnapshots()
    {
        var memory = new ReleaseMemoryOptionDescriptor
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Scope = ReleaseMemoryScopeKind.AllowOnce,
            SafeDisplayName = "Once",
        };
        var secondMemory = memory with { Identifier = new() { Value = Guid.NewGuid() } };
        var source = new CredentialSourceChoiceDescriptor
        {
            Identifier = new() { Digest = new string('a', 64) },
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialSourceKind.Provider,
            Usability = CredentialSourceUsability.AcquisitionRequired,
            SafeDisplayName = "Provider",
        };
        var secondSource = source with { Identifier = new() { Digest = new string('b', 64) } };
        var snapshot = Snapshot() with
        {
            MemoryOptions = [memory, secondMemory], SelectedMemory = memory.Identifier,
            SourceOptions = [source, secondSource], SelectedSource = source.Identifier,
        };
        var registry = new Registry { Snapshot = [snapshot] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var row = Assert.Single(rows.Rows);
        row.SelectedMemoryOption = secondMemory;
        row.SelectedSourceOption = secondSource;
        registry.Snapshot = [snapshot with { Revision = 3 }];
        registry.Notify();
        registry.Snapshot = [snapshot with { Revision = 2, CanAct = false }];
        registry.Notify();
        Assert.Equal(3, row.Revision);
        Assert.True(row.CanAct);
        await row.ApplyCommand.ExecuteAsync(null);
        var action = Assert.IsType<ApplyUserAuthorizationAction>(registry.Action);
        Assert.Equal(secondMemory.Identifier, action.Memory);
        Assert.Equal(secondSource.Identifier, action.Source);
        registry.Snapshot = [snapshot with { Revision = 4, SourceOptions = [source] }];
        registry.Notify();
        Assert.Equal(source.Identifier, row.SelectedSource);
    }

    [Fact]
    public async Task StaleActionRestoresCommandAndShutdownMakesItInert()
    {
        var completion = new TaskCompletionSource();
        var registry = new Registry
        {
            Snapshot = [Snapshot()],
            ExecuteAction = _ => completion.Task,
        };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var row = Assert.Single(rows.Rows);
        var action = row.ApplyCommand.ExecuteAsync(null);
        Assert.False(row.CanAct);
        completion.SetException(new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.StaleRevision));
        await action;
        Assert.True(row.CanAct);
        registry.ExecuteAction = _ => throw new ObjectDisposedException("registry");
        await row.ApplyCommand.ExecuteAsync(null);
        Assert.False(row.CanAct);
    }

    [Theory]
    [InlineData(UserAuthorizationState.Pending, CredentialSourceUsability.Usable, "Allow")]
    [InlineData(UserAuthorizationState.Pending, CredentialSourceUsability.AcquisitionRequired, "Perform")]
    [InlineData(UserAuthorizationState.Failed, CredentialSourceUsability.Usable, "Retry (Allow)")]
    [InlineData(UserAuthorizationState.Failed, CredentialSourceUsability.AcquisitionRequired, "Retry (Perform)")]
    [InlineData(UserAuthorizationState.RenewalNeeded, CredentialSourceUsability.Usable, "Allow")]
    [InlineData(UserAuthorizationState.RenewalNeeded, CredentialSourceUsability.AcquisitionRequired, "Perform")]
    public void PrimaryActionLabelUsesCurrentSelectedSourceAndRowState(
        UserAuthorizationState state,
        CredentialSourceUsability usability,
        string expected)
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            State = state,
            PrimaryAction = UserAuthorizationPrimaryActionKind.Allow,
            SourceOptions = [snapshot.SourceOptions[0] with { Usability = usability }],
        };
        var registry = new Registry { Snapshot = [snapshot] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        Assert.Equal(expected, Assert.Single(rows.Rows).PrimaryActionLabel);
    }

    [Fact]
    public void LocalSelectionAndMetadataUpdatesNotifyAllowPerformInBothDirections()
    {
        var snapshot = Snapshot();
        var acquisition = snapshot.SourceOptions[0] with
        {
            Identifier = new() { Digest = new string('b', 64) },
            Usability = CredentialSourceUsability.AcquisitionRequired,
        };
        snapshot = snapshot with { SourceOptions = [snapshot.SourceOptions[0], acquisition] };
        var registry = new Registry { Snapshot = [snapshot] };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var row = Assert.Single(rows.Rows);
        var labels = new List<string>();
        row.PropertyChanged += (_, arguments) =>
        {
            if (string.IsNullOrEmpty(arguments.PropertyName) || arguments.PropertyName == nameof(row.PrimaryActionLabel))
            {
                labels.Add(row.PrimaryActionLabel);
            }
        };
        Assert.Equal("Allow", row.PrimaryActionLabel);
        row.SelectedSourceOption = acquisition;
        Assert.Equal("Perform", row.PrimaryActionLabel);
        row.SelectedSource = snapshot.SelectedSource;
        Assert.Equal("Allow", row.PrimaryActionLabel);
        row.SelectedSource = acquisition.Identifier;
        registry.Snapshot = [snapshot with
        {
            Revision = 3,
            SourceOptions = [snapshot.SourceOptions[0], acquisition with { Usability = CredentialSourceUsability.Usable }],
        }];
        registry.Notify();
        Assert.Equal("Allow", row.PrimaryActionLabel);
        Assert.Equal(acquisition.Identifier, row.SelectedSource);
        registry.Snapshot = [snapshot with { Revision = 2 }];
        registry.Notify();
        Assert.Equal(3, row.Revision);
        Assert.Equal("Allow", row.PrimaryActionLabel);
        Assert.Contains("Perform", labels);
        Assert.Equal("Allow", labels[^1]);
    }

    [Fact]
    public async Task DisplayedPerformMatchesActualRegistryDecisionAndStaleActionCannotGrantAgain()
    {
        var snapshot = Snapshot();
        var acquisition = snapshot.SourceOptions[0] with
        {
            Identifier = new() { Digest = new string('b', 64) },
            Usability = CredentialSourceUsability.AcquisitionRequired,
        };
        snapshot = snapshot with { SourceOptions = [snapshot.SourceOptions[0], acquisition] };
        await using var registry = new UserAuthorizationRegistry();
        registry.Add(snapshot);
        var callbacks = new List<Action>();
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = callbacks.Add, Registry = registry };
        callbacks[0]();
        var row = Assert.Single(rows.Rows);
        row.SelectedSourceOption = acquisition;
        Assert.Equal("Perform", row.PrimaryActionLabel);
        await row.ApplyCommand.ExecuteAsync(null);
        var decision = await registry.WaitForDecisionAsync(snapshot.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, decision.PrimaryAction);
        Assert.Equal(acquisition.Identifier, decision.Source);
        Assert.Equal(snapshot.Revision + 1, decision.Revision);
        var performing = Assert.Single(registry.GetSnapshot());
        await row.ApplyCommand.ExecuteAsync(null);
        Assert.Same(performing, Assert.Single(registry.GetSnapshot()));
        callbacks[1]();
        Assert.False(row.CanAct);
        Assert.Equal("Unavailable", row.PrimaryActionLabel);
    }
}

using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class AuthorizationRowsConstructionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInitialDispatchUnsubscribesAndMakesQueuedProjectionInert(bool enqueue)
    {
        var registry = new TrackingRegistry();
        var callbacks = new List<Action>();
        var expected = new InvalidOperationException("Dispatcher unavailable.");
        var dispatches = 0;
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new AuthorizationRequestRowsViewModel
            {
                Dispatch = action =>
                {
                    dispatches++;
                    if (enqueue) { callbacks.Add(action); }
                    throw expected;
                },
                Registry = registry,
            });

        Assert.Same(expected, failure);
        Assert.Equal(0, registry.Subscribers);
        Assert.Equal(1, dispatches);
        var rows = Assert.IsType<AuthorizationRequestRowsViewModel>(registry.SubscribedRows);
        registry.Notify();
        foreach (var callback in callbacks) { callback(); }
        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        rows.Dispose();
        Assert.Empty(rows.Rows);
        Assert.Equal(1, dispatches);
        Assert.Equal(0, registry.Subscribers);
    }

    private sealed class TrackingRegistry : IUserAuthorizationRegistry
    {
        private EventHandler<UserAuthorizationChangedEventArgs>? changed;
        internal AuthorizationRequestRowsViewModel? SubscribedRows { get; private set; }
        internal int Subscribers => this.changed?.GetInvocationList().Length ?? 0;
        public event EventHandler<UserAuthorizationChangedEventArgs>? Changed
        {
            add
            {
                this.SubscribedRows = value?.Target as AuthorizationRequestRowsViewModel;
                this.changed += value;
            }
            remove => this.changed -= value;
        }

        public IReadOnlyList<UserAuthorizationSnapshot> GetSnapshot()
        {
            Assert.Equal(1, this.Subscribers);
            return [AuthorizationRequestRowsViewModelTests.Snapshot()];
        }

        internal void Notify() => this.changed?.Invoke(this, new UserAuthorizationChangedEventArgs
        {
            Identifier = new UserAuthorizationRequestIdentifier { Value = Guid.Empty },
            Revision = 2,
        });

        public Task ActAsync(UserAuthorizationAction action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<UserAuthorizationCompletion> WaitForCompletionAsync(
            UserAuthorizationRequestIdentifier identifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

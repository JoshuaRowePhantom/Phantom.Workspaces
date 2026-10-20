using System.Threading.Channels;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.Services.Authorization;

internal sealed record UserAuthorizationDecision
{
    public required UserAuthorizationRequestIdentifier Identifier { get; init; }
    public required long Revision { get; init; }
    public required UserAuthorizationState State { get; init; }
    public required ReleaseMemoryScopeKind Memory { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
    public required UserAuthorizationPrimaryActionKind PrimaryAction { get; init; }
}

public sealed class UserAuthorizationRegistry : IUserAuthorizationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<UserAuthorizationRequestIdentifier, Row> rows = new();
    private bool disposed;

    public event EventHandler<UserAuthorizationChangedEventArgs>? Changed;

    public IReadOnlyList<UserAuthorizationSnapshot> GetSnapshot()
    {
        lock (this.gate)
        {
            return this.rows.Values.Select(row => row.Snapshot).ToArray();
        }
    }

    public Task ActAsync(
        UserAuthorizationAction action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserAuthorizationSnapshot updated;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.rows.TryGetValue(action.RequestIdentifier, out var row))
            {
                throw new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.Inert);
            }

            var snapshot = row.Snapshot;
            if (snapshot.Revision != action.ExpectedRevision)
            {
                throw new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.StaleRevision);
            }

            if (!snapshot.CanAct)
            {
                File.AppendAllText(@"C:\dev\Phantom.Workspaces-Skills\test-debug.log", $"DEBUG ActAsync: CanAct check failed. snapshot.CanAct={snapshot.CanAct}, State={snapshot.State}, Revision={snapshot.Revision}{Environment.NewLine}");
                throw new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.Inert);
            }


            var memory = action is ApplyUserAuthorizationAction apply ? apply.Memory : snapshot.SelectedMemory;
            var source = action is ApplyUserAuthorizationAction selection ? selection.Source : snapshot.SelectedSource;
            var memoryOption = snapshot.MemoryOptions.SingleOrDefault(option => option.Identifier == memory);
            var sourceOption = snapshot.SourceOptions.SingleOrDefault(option => option.Identifier == source);
            if (memoryOption is null || sourceOption is null ||
                (action is RetryUserAuthorizationAction && snapshot.State is not (UserAuthorizationState.Failed or UserAuthorizationState.RenewalNeeded)))
            {
                throw new UserAuthorizationActionRejectedException(UserAuthorizationActionRejection.InvalidSelection);
            }

            var state = action switch
            {
                DenyUserAuthorizationAction => UserAuthorizationState.Denied,
                CancelUserAuthorizationAction => UserAuthorizationState.Cancelled,
                _ => UserAuthorizationState.Performing,
            };
            var primaryAction = sourceOption.Usability == CredentialSourceUsability.Usable
                ? UserAuthorizationPrimaryActionKind.Allow
                : UserAuthorizationPrimaryActionKind.Perform;
            updated = snapshot with
            {
                Revision = snapshot.Revision + 1,
                State = state,
                SelectedMemory = memory,
                SelectedSource = source,
                PrimaryAction = null,
                CanAct = false,
                SafeStatus = Status(state),
            };
            row.Snapshot = updated;
            if (row.Completion.Task.IsCompleted && state == UserAuthorizationState.Performing)
            {
                row.Completion = NewCompletion();
            }

            row.Decisions.Writer.TryWrite(new UserAuthorizationDecision
            {
                Identifier = updated.Identifier,
                Revision = updated.Revision,
                State = updated.State,
                Memory = memoryOption.Scope,
                Source = source,
                PrimaryAction = primaryAction,
            });
            if (state != UserAuthorizationState.Performing)
            {
                row.Completion.TrySetResult(Completion(updated));
            }
        }

        this.Notify(updated);
        return Task.CompletedTask;
    }

    public Task<UserAuthorizationCompletion> WaitForCompletionAsync(
        UserAuthorizationRequestIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return this.rows[identifier].Completion.Task.WaitAsync(cancellationToken);
        }
    }

    internal void Add(UserAuthorizationSnapshot snapshot)
    {
        var frozen = snapshot with
        {
            MemoryOptions = Array.AsReadOnly(snapshot.MemoryOptions.ToArray()),
            SourceOptions = Array.AsReadOnly(snapshot.SourceOptions.ToArray()),
            SafeStatus = Status(snapshot.State),
        };
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.rows.Add(snapshot.Identifier, new Row { Snapshot = frozen });
        }

        this.Notify(frozen);
    }

    internal ValueTask<UserAuthorizationDecision> WaitForDecisionAsync(
        UserAuthorizationRequestIdentifier identifier,
        CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return this.rows[identifier].Decisions.Reader.ReadAsync(cancellationToken);
        }
    }

    internal bool UpdateSources(
        UserAuthorizationRequestIdentifier identifier,
        long expectedRevision,
        IReadOnlyList<CredentialSourceChoiceDescriptor> sources)
    {
        UserAuthorizationSnapshot updated;
        lock (this.gate)
        {
            if (this.disposed || !this.rows.TryGetValue(identifier, out var row) ||
                row.Snapshot.Revision != expectedRevision || !row.Snapshot.CanAct)
            {
                return false;
            }

            var selected = sources.Single(source => source.Identifier == row.Snapshot.SelectedSource);
            updated = row.Snapshot with
            {
                Revision = expectedRevision + 1,
                SourceOptions = Array.AsReadOnly(sources.ToArray()),
                PrimaryAction = selected.Usability == CredentialSourceUsability.Usable
                    ? UserAuthorizationPrimaryActionKind.Allow : UserAuthorizationPrimaryActionKind.Perform,
                CanAct = row.Snapshot.CanAct,
            };
            row.Snapshot = updated;
        }

        this.Notify(updated);
        return true;
    }

    internal bool Complete(
        UserAuthorizationRequestIdentifier identifier,
        long expectedRevision,
        UserAuthorizationState state)
    {
        if (state is not (UserAuthorizationState.Completed or UserAuthorizationState.Failed or
            UserAuthorizationState.RenewalNeeded or UserAuthorizationState.Denied or UserAuthorizationState.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        UserAuthorizationSnapshot updated;
        lock (this.gate)
        {
            if (this.disposed || !this.rows.TryGetValue(identifier, out var row) ||
                row.Snapshot.Revision != expectedRevision ||
                row.Snapshot.State is UserAuthorizationState.Completed or UserAuthorizationState.Denied or UserAuthorizationState.Cancelled)
            {
                File.AppendAllText(@"C:\dev\Phantom.Workspaces-Skills\test-debug.log", $"DEBUG Complete: Rejected. disposed={this.disposed}, hasRow={this.rows.TryGetValue(identifier, out _)}, expectedRevision={expectedRevision}{Environment.NewLine}");
                return false;
            }

            var newCanAct = state is UserAuthorizationState.Failed or UserAuthorizationState.RenewalNeeded;
            updated = row.Snapshot with
            {
                Revision = expectedRevision + 1,
                State = state,
                CanAct = newCanAct,
                PrimaryAction = state switch
                {
                    UserAuthorizationState.Failed => UserAuthorizationPrimaryActionKind.Retry,
                    UserAuthorizationState.RenewalNeeded => UserAuthorizationPrimaryActionKind.Perform,
                    _ => null,
                },
                SafeStatus = Status(state),
            };
            row.Snapshot = updated;
            File.AppendAllText(@"C:\dev\Phantom.Workspaces-Skills\test-debug.log", $"DEBUG Complete: Success. State={state}, CanAct={newCanAct}, newRevision={expectedRevision + 1}{Environment.NewLine}");
            if (state != UserAuthorizationState.RenewalNeeded)
            {
                row.Completion.TrySetResult(Completion(updated));
            }
        }

        this.Notify(updated);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        EventHandler<UserAuthorizationChangedEventArgs>? changed;
        UserAuthorizationSnapshot[] previous;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return ValueTask.CompletedTask;
            }

            this.disposed = true;
            previous = this.rows.Values.Select(row => row.Snapshot).ToArray();
            foreach (var row in this.rows.Values)
            {
                row.Completion.TrySetResult(Completion(row.Snapshot with { State = UserAuthorizationState.Cancelled }));
                row.Decisions.Writer.TryComplete();
            }

            this.rows.Clear();
            changed = this.Changed;
            this.Changed = null;
        }

        foreach (var snapshot in previous)
        {
            changed?.Invoke(this, new UserAuthorizationChangedEventArgs
            {
                Identifier = snapshot.Identifier,
                Revision = snapshot.Revision + 1,
            });
        }

        return ValueTask.CompletedTask;
    }

    private void Notify(UserAuthorizationSnapshot snapshot) =>
        this.Changed?.Invoke(this, new UserAuthorizationChangedEventArgs { Identifier = snapshot.Identifier, Revision = snapshot.Revision });

    private static TaskCompletionSource<UserAuthorizationCompletion> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static UserAuthorizationCompletion Completion(UserAuthorizationSnapshot snapshot) => new()
    {
        Identifier = snapshot.Identifier,
        Revision = snapshot.Revision,
        State = snapshot.State,
    };

    private static string Status(UserAuthorizationState state) => state switch
    {
        UserAuthorizationState.Pending => "Authorization required.",
        UserAuthorizationState.Performing => "Authorization in progress.",
        UserAuthorizationState.Completed => "Authorization completed.",
        UserAuthorizationState.Failed => "Authorization failed.",
        UserAuthorizationState.RenewalNeeded => "Renew authentication.",
        UserAuthorizationState.Denied => "Authorization denied.",
        UserAuthorizationState.Cancelled => "Authorization cancelled.",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private sealed class Row
    {
        public required UserAuthorizationSnapshot Snapshot { get; set; }
        public TaskCompletionSource<UserAuthorizationCompletion> Completion { get; set; } = NewCompletion();
        public Channel<UserAuthorizationDecision> Decisions { get; } = Channel.CreateUnbounded<UserAuthorizationDecision>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false });
    }
}

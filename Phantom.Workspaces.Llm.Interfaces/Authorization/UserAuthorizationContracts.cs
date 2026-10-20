using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Interfaces.Authorization;

public enum UserAuthorizationState { Pending, Performing, Completed, Failed, RenewalNeeded, Denied, Cancelled }
public enum UserAuthorizationPrimaryActionKind { Allow, Perform, Retry }

public sealed record UserAuthorizationSnapshot
{
    public required UserAuthorizationRequestIdentifier Identifier { get; init; }
    public required long Revision { get; init; }
    public required string UseDisplayName { get; init; }
    public required UserAuthorizationState State { get; init; }
    public UserAuthorizationPrimaryActionKind? PrimaryAction { get; init; }
    public required IReadOnlyList<ReleaseMemoryOptionDescriptor> MemoryOptions { get; init; }
    public required ReleaseMemoryOptionIdentifier SelectedMemory { get; init; }
    public required IReadOnlyList<CredentialSourceChoiceDescriptor> SourceOptions { get; init; }
    public required CredentialSourceIdentifier SelectedSource { get; init; }
    public required string SafeStatus { get; init; }
    public required bool IsBootstrapBlocking { get; init; }
    public required bool CanAct { get; init; }
}

public abstract record UserAuthorizationAction
{
    private protected UserAuthorizationAction() { }
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
    public required long ExpectedRevision { get; init; }
}
public sealed record ApplyUserAuthorizationAction : UserAuthorizationAction
{
    public required ReleaseMemoryOptionIdentifier Memory { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
}
public sealed record DenyUserAuthorizationAction : UserAuthorizationAction;
public sealed record RetryUserAuthorizationAction : UserAuthorizationAction;
public sealed record CancelUserAuthorizationAction : UserAuthorizationAction;

public sealed record UserAuthorizationCompletion
{
    public required UserAuthorizationRequestIdentifier Identifier { get; init; }
    public required long Revision { get; init; }
    public required UserAuthorizationState State { get; init; }
}

public sealed class UserAuthorizationChangedEventArgs : EventArgs
{
    public required UserAuthorizationRequestIdentifier Identifier { get; init; }
    public required long Revision { get; init; }
}

public enum UserAuthorizationActionRejection { StaleRevision, Inert, InvalidSelection }

public sealed class UserAuthorizationActionRejectedException : Exception
{
    public UserAuthorizationActionRejectedException(UserAuthorizationActionRejection reason)
        : base("The authorization action is no longer applicable.")
    {
        this.Reason = reason;
    }

    public UserAuthorizationActionRejection Reason { get; }
}

public interface IUserAuthorizationRegistry : IAsyncDisposable
{
    event EventHandler<UserAuthorizationChangedEventArgs>? Changed;
    IReadOnlyList<UserAuthorizationSnapshot> GetSnapshot();
    Task ActAsync(
        UserAuthorizationAction action,
        CancellationToken cancellationToken = default);
    Task<UserAuthorizationCompletion> WaitForCompletionAsync(
        UserAuthorizationRequestIdentifier identifier,
        CancellationToken cancellationToken = default);
}

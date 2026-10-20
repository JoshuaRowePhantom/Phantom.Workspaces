namespace Phantom.Workspaces.Llm.Interfaces.Access;

public interface IAccessResult : IAsyncDisposable;

public interface ICredentialAccessResult : IAccessResult;

public sealed record UserAuthorizationRequestIdentifier
{
    public required Guid Value { get; init; }
}

public enum AccessDenialReason
{
    UserDenied,
    PolicyDenied,
    UnsupportedSource,
}

public enum AccessErrorCode
{
    UnsupportedRequest,
    DependencyCycle,
    ProviderFailure,
    PersistenceFailure,
    IdentityMismatch,
    InvalidGeneration,
    Disposed,
}

public abstract record AccessOutcome<TResult>
    where TResult : IAccessResult
{
    private protected AccessOutcome() { }
}

public sealed record AccessSucceeded<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult
{
    public required TResult Result { get; init; }
}

public sealed record AccessDenied<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult
{
    public required AccessDenialReason Reason { get; init; }
}

public sealed record AccessCancelled<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult;

public sealed record AccessAuthorizationRequired<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}

public sealed record AccessReacquisitionRequired<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}

public sealed record AccessFailed<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult
{
    public required AccessErrorCode ErrorCode { get; init; }
    public required string SafeMessage { get; init; }
    public required bool CanRetry { get; init; }
}

public sealed record AccessShuttingDown<TResult> : AccessOutcome<TResult>
    where TResult : IAccessResult;

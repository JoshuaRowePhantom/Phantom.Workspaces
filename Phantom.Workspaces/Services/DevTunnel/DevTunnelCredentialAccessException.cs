using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class DevTunnelCredentialAccessException : InvalidOperationException
{
    internal DevTunnelCredentialAccessException(
        AccessErrorCode errorCode,
        UserAuthorizationRequestIdentifier? requiredAction = null)
        : base("Dev Tunnel credential access did not complete.")
    {
        this.ErrorCode = errorCode;
        this.RequiredAction = requiredAction;
    }

    public AccessErrorCode ErrorCode { get; }
    public UserAuthorizationRequestIdentifier? RequiredAction { get; }

    internal static async ValueTask<TResult> RequireAsync<TResult>(
        AccessOutcome<TResult> outcome,
        CancellationToken cancellationToken) where TResult : IAccessResult
    {
        if (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (outcome is AccessSucceeded<TResult> late)
                {
                    await late.Result.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return outcome switch
        {
            AccessSucceeded<TResult> succeeded => succeeded.Result,
            AccessFailed<TResult> failed => throw new DevTunnelCredentialAccessException(failed.ErrorCode),
            AccessCancelled<TResult> => throw new OperationCanceledException(
                "Dev Tunnel credential access was cancelled.", cancellationToken),
            AccessShuttingDown<TResult> => throw new OperationCanceledException(
                "Dev Tunnel credential access is shutting down.", cancellationToken),
            _ => throw new DevTunnelCredentialAccessException(AccessErrorCode.ProviderFailure),
        };
    }

    internal static TResult RequireUse<TResult>(SensitiveValueUseOutcome<TResult> outcome) =>
        outcome switch
        {
            SensitiveValueUseSucceeded<TResult> succeeded => succeeded.Result,
            SensitiveValueUseAuthorizationRequired<TResult> required =>
                throw new DevTunnelCredentialAccessException(AccessErrorCode.ProviderFailure, required.RequestIdentifier),
            SensitiveValueUseReacquisitionRequired<TResult> required =>
                throw new DevTunnelCredentialAccessException(AccessErrorCode.InvalidGeneration, required.RequestIdentifier),
            SensitiveValueUseFailed<TResult> failed => throw new DevTunnelCredentialAccessException(failed.ErrorCode),
            _ => throw new DevTunnelCredentialAccessException(AccessErrorCode.ProviderFailure),
        };
}

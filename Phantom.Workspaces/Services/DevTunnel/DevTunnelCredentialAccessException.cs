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
            if (outcome is AccessSucceeded<TResult> late)
            {
                await late.Result.DisposeAsync().ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
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

    internal static async ValueTask<TResult> RequireUseAsync<TResult>(
        SensitiveValueUseOutcome<TResult> outcome,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            if (outcome is SensitiveValueUseSucceeded<TResult> late)
            {
                if (late.Result is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (late.Result is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        return outcome switch
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
}

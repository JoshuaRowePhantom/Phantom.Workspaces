using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record CredentialLeaseBinding
{
    public required ReleaseAuthorizationIdentifier AuthorizationIdentifier { get; init; }
    public required ReleasePolicyRequest Request { get; init; }
    public required CredentialMaterialGenerationIdentifier MaterialGeneration { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }
}

public enum CredentialLeaseRequiredActionKind { Authorization, Reacquisition }

public interface ICredentialLeaseContext
{
    IReleaseAuthorizationPolicy Policy { get; }

    ValueTask<bool> IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken);

    ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
        ReleasePolicyRequest request,
        CredentialLeaseRequiredActionKind action,
        CancellationToken cancellationToken);
}

public abstract record SensitiveValueUseOutcome<TResult>
{
    private protected SensitiveValueUseOutcome() { }
}
public sealed record SensitiveValueUseSucceeded<TResult> : SensitiveValueUseOutcome<TResult>
{
    public required TResult Result { get; init; }
}
public sealed record SensitiveValueUseAuthorizationRequired<TResult> : SensitiveValueUseOutcome<TResult>
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}
public sealed record SensitiveValueUseReacquisitionRequired<TResult> : SensitiveValueUseOutcome<TResult>
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}
public sealed record SensitiveValueUseDenied<TResult> : SensitiveValueUseOutcome<TResult>;
public sealed record SensitiveValueUseFailed<TResult> : SensitiveValueUseOutcome<TResult>
{
    public required AccessErrorCode ErrorCode { get; init; }
    public required string SafeMessage { get; init; }
}

public sealed class SensitiveValueLease : IAsyncDisposable
{
    private readonly char[] material;
    private readonly CredentialLeaseBinding binding;
    private readonly LeaseUseGuard guard;
    private readonly SensitiveValueLease? underlyingLease;
    private readonly IAsyncDisposable? underlyingResultOwner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    internal CredentialAccountIdentifier? AccountIdentifier => this.binding.Request.Credential.AccountIdentifier;

    public SensitiveValueLease(
        ReadOnlyMemory<char> material,
        CredentialLeaseBinding binding,
        ICredentialLeaseContext context,
        TimeProvider? timeProvider = null)
    {
        this.material = material.ToArray();
        this.binding = binding;
        this.guard = new LeaseUseGuard(binding, context, timeProvider ?? TimeProvider.System);
    }

    private SensitiveValueLease(
        SensitiveValueLease underlyingLease,
        IAsyncDisposable underlyingResultOwner,
        CredentialLeaseBinding binding,
        ICredentialLeaseContext context,
        TimeProvider timeProvider)
    {
        this.material = [];
        this.binding = binding;
        this.guard = new LeaseUseGuard(binding, context, timeProvider);
        this.underlyingLease = underlyingLease;
        this.underlyingResultOwner = underlyingResultOwner;
    }

    internal static SensitiveValueLease CreateAuthorizedView(
        SensitiveValueLease underlyingLease,
        IAsyncDisposable underlyingResultOwner,
        ReleaseAuthorizationIdentifier authorizationIdentifier,
        ICredentialExecutionContext executionContext,
        TimeProvider? timeProvider = null)
    {
        var request = executionContext.PolicyRequest
            ?? throw new InvalidOperationException("Completion-only authorization cannot export a credential lease.");
        return new SensitiveValueLease(
            underlyingLease,
            underlyingResultOwner,
            new CredentialLeaseBinding
            {
                AuthorizationIdentifier = authorizationIdentifier,
                Request = request,
                MaterialGeneration = underlyingLease.binding.MaterialGeneration,
            },
            executionContext.CreateLeaseContext(new UnderlyingLeaseGenerationValidator(underlyingLease)),
            timeProvider ?? TimeProvider.System);
    }

    public async ValueTask<SensitiveValueUseOutcome<TResult>> UseAsync<TResult>(
        Func<string, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return new SensitiveValueUseFailed<TResult> { ErrorCode = AccessErrorCode.Disposed, SafeMessage = "The credential lease is disposed." };
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var validation = await this.guard.ValidateAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return validation.Kind switch
            {
                LeaseValidationKind.AuthorizationRequired => new SensitiveValueUseAuthorizationRequired<TResult> { RequestIdentifier = validation.RequestIdentifier! },
                LeaseValidationKind.ReacquisitionRequired => new SensitiveValueUseReacquisitionRequired<TResult> { RequestIdentifier = validation.RequestIdentifier! },
                LeaseValidationKind.Denied => new SensitiveValueUseDenied<TResult>(),
                LeaseValidationKind.Failed => new SensitiveValueUseFailed<TResult>
                {
                    ErrorCode = AccessErrorCode.PersistenceFailure,
                    SafeMessage = "Release authorization could not be validated.",
                },
                _ when this.underlyingLease is not null =>
                    await this.underlyingLease.UseAsync(operation, cancellation.Token).ConfigureAwait(false),
                _ => new SensitiveValueUseSucceeded<TResult>
                {
                    Result = await operation(new string(this.material), cancellation.Token).ConfigureAwait(false),
                },
            };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            Exception? cancellationFailure = null;
            try
            {
                await this.lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            try
            {
                await this.gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.material.AsSpan()));
                    if (this.underlyingResultOwner is not null)
                    {
                        await this.underlyingResultOwner.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    this.gate.Release();
                }
            }
            catch (Exception exception) when (cancellationFailure is not null)
            {
                throw new AggregateException("Credential lease cleanup failed.", cancellationFailure, exception);
            }

            if (cancellationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            }

            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }

    private sealed class UnderlyingLeaseGenerationValidator(
        SensitiveValueLease underlyingLease) : ICredentialGenerationValidator
    {
        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The inner use checks its live source only after its own release policy has passed.
            return ValueTask.FromResult(
                generation == underlyingLease.binding.MaterialGeneration
                && Volatile.Read(ref underlyingLease.disposed) == 0);
        }
    }
}

internal enum LeaseValidationKind { Valid, AuthorizationRequired, ReacquisitionRequired, Denied, Failed }
internal sealed record LeaseValidation
{
    public required LeaseValidationKind Kind { get; init; }
    public UserAuthorizationRequestIdentifier? RequestIdentifier { get; init; }
}

internal sealed class LeaseUseGuard(
    CredentialLeaseBinding binding,
    ICredentialLeaseContext context,
    TimeProvider timeProvider)
{
    public async ValueTask<LeaseValidation> ValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await this.ValidateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ReleaseIdentityMismatchException)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Denied };
        }
        catch (System.IO.IOException)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Failed };
        }
        catch (UnauthorizedAccessException)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Failed };
        }
        catch (System.Text.Json.JsonException)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Failed };
        }
        catch (ObjectDisposedException)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Denied };
        }
    }

    private async ValueTask<LeaseValidation> ValidateCoreAsync(CancellationToken cancellationToken)
    {
        var validation = await context.Policy.ValidateAsync(binding.AuthorizationIdentifier, binding.Request, cancellationToken).ConfigureAwait(false);
        if (validation == ReleaseAuthorizationValidation.IdentityMismatch)
        {
            return new LeaseValidation { Kind = LeaseValidationKind.Denied };
        }

        if (validation == ReleaseAuthorizationValidation.AuthorizationRequired)
        {
            return new LeaseValidation
            {
                Kind = LeaseValidationKind.AuthorizationRequired,
                RequestIdentifier = await context.RequireAsync(binding.Request, CredentialLeaseRequiredActionKind.Authorization, cancellationToken).ConfigureAwait(false),
            };
        }

        if ((binding.ExpiresOn is { } expiresOn && timeProvider.GetUtcNow() >= expiresOn) ||
            !await context.IsCurrentGenerationAsync(binding.MaterialGeneration, cancellationToken).ConfigureAwait(false))
        {
            return new LeaseValidation
            {
                Kind = LeaseValidationKind.ReacquisitionRequired,
                RequestIdentifier = await context.RequireAsync(binding.Request, CredentialLeaseRequiredActionKind.Reacquisition, cancellationToken).ConfigureAwait(false),
            };
        }

        return new LeaseValidation { Kind = LeaseValidationKind.Valid };
    }
}

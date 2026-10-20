using System.ComponentModel;
using System.Security;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthCacheCommitCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CredentialManagerTokenCache cache;
    private readonly ProviderAcquisitionKey acquisitionKey;
    private ProviderAcquisitionGeneration? current;
    private bool disposed;

    internal McpOAuthCacheCommitCoordinator(
        CredentialManagerTokenCache cache,
        ProviderAcquisitionKey acquisitionKey)
    {
        this.cache = cache;
        this.acquisitionKey = acquisitionKey;
    }

    internal async ValueTask<ProviderAcquisitionGeneration> BeginGenerationAsync(
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!commitContext.InteractionPermit.AuthorizesProvider(CredentialExecutorKind.McpOAuth, this.acquisitionKey) ||
                commitContext.CommittedGeneration is not null)
            {
                throw new InvalidOperationException("OAuth cache acquisition is not authorized.");
            }

            var generation = new ProviderAcquisitionGeneration(this, commitContext);
            await this.cache.ReserveGenerationAsync(generation.MaterialGeneration, cancellationToken).ConfigureAwait(false);
            this.current?.Completion.TrySetCanceled();
            this.current = generation;
            return this.current;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask StoreAndPublishAsync(
        TokenContainer tokens,
        ProviderAcquisitionGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await this.gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation.Owner == this && ReferenceEquals(generation, this.current) &&
                    !generation.Completion.Task.IsCompleted)
                {
                    generation.Outcome = new ProviderAcquisitionCancelled();
                    generation.Completion.TrySetCanceled();
                }
            }
            finally
            {
                this.gate.Release();
            }
            throw new OperationCanceledException("OAuth cache acquisition was cancelled.");
        }
        try
        {
            if (this.disposed || generation.Owner != this || !ReferenceEquals(generation, this.current) ||
                generation.Completion.Task.IsCompleted)
            {
                throw new InvalidOperationException("OAuth cache generation is no longer available.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                generation.Outcome = await generation.CommitContext.CommitAsync(
                    generation.MaterialGeneration,
                    cancellation => this.cache.StoreGenerationAsync(tokens, generation.MaterialGeneration, cancellation))
                    .ConfigureAwait(false);
                if (generation.Outcome is ProviderAcquisitionSucceeded)
                {
                    generation.Completion.TrySetResult();
                }
                else
                {
                    generation.Completion.TrySetCanceled();
                    throw new OperationCanceledException("OAuth cache acquisition was cancelled.");
                }
            }
            catch (OperationCanceledException)
            {
                generation.Outcome = new ProviderAcquisitionCancelled();
                generation.Completion.TrySetCanceled();
                throw new OperationCanceledException("OAuth cache acquisition was cancelled.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
                Win32Exception or JsonException)
            {
                generation.Outcome = new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.PersistenceFailure };
                generation.Completion.TrySetException(new InvalidOperationException("OAuth cache commit failed."));
                throw new InvalidOperationException("OAuth cache commit failed.");
            }
            catch (InvalidOperationException)
            {
                generation.Outcome = new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
                generation.Completion.TrySetException(new InvalidOperationException("OAuth cache generation is no longer available."));
                throw new InvalidOperationException("OAuth cache generation is no longer available.");
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    public Task WaitForCommitAsync(
        ProviderAcquisitionGeneration generation,
        CancellationToken cancellationToken)
    {
        if (generation.Owner != this)
        {
            throw new InvalidOperationException("OAuth cache generation does not belong to this cache.");
        }

        return generation.Completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            this.disposed = true;
            this.current?.Completion.TrySetCanceled();
            this.current = null;
        }
        finally
        {
            this.gate.Release();
        }
    }
}

internal sealed class ProviderAcquisitionGeneration
{
    internal ProviderAcquisitionGeneration(
        McpOAuthCacheCommitCoordinator owner,
        ProviderAcquisitionCommitContext commitContext)
    {
        this.Owner = owner;
        this.CommitContext = commitContext;
    }

    internal McpOAuthCacheCommitCoordinator Owner { get; }
    internal ProviderAcquisitionCommitContext CommitContext { get; }
    internal CredentialMaterialGenerationIdentifier MaterialGeneration { get; } = new() { Value = Guid.NewGuid() };
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ProviderAcquisitionOutcome? Outcome { get; set; }
}

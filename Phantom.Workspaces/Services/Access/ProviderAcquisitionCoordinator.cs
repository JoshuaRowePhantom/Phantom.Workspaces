using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal sealed class ProviderAcquisitionCoordinator : IProviderAcquisitionCoordinator
{
    private readonly object gate = new();
    private readonly Dictionary<ProviderAcquisitionKey, Acquisition> acquisitions = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;

    public async ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ProviderAcquisitionCancelled();
        }

        Acquisition acquisition;
        var start = false;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return new ProviderAcquisitionShuttingDown();
            }

            if (!permit.TryConsume(operation))
            {
                return new ProviderAcquisitionDenied();
            }

            if (!this.acquisitions.TryGetValue(operation.CompatibilityKey, out acquisition!))
            {
                acquisition = new Acquisition();
                this.acquisitions.Add(operation.CompatibilityKey, acquisition);
                start = true;
            }

            acquisition.Participants++;
        }

        if (start)
        {
            _ = this.RunAsync(operation, acquisition, permit);
        }

        try
        {
            return await acquisition.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProviderAcquisitionCancelled();
        }
        finally
        {
            Task cancellationTask = Task.CompletedTask;
            await acquisition.CommitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cancel = false;
                lock (this.gate)
                {
                    acquisition.Participants--;
                    if (acquisition.Participants == 0 && !acquisition.Completion.Task.IsCompleted)
                    {
                        acquisition.Tombstoned = true;
                        this.acquisitions.Remove(operation.CompatibilityKey);
                        cancel = true;
                    }
                }

                if (cancel)
                {
                    cancellationTask = acquisition.Cancellation.CancelAsync();
                }
            }
            finally
            {
                acquisition.CommitGate.Release();
            }

            await cancellationTask.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Acquisition[]? active = null;
        lock (this.gate)
        {
            if (!this.disposed)
            {
                this.disposed = true;
                active = this.acquisitions.Values.ToArray();
                this.acquisitions.Clear();
            }
        }

        if (active is null)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.WhenAll(active.Select(this.ShutdownAsync)).ConfigureAwait(false);
            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }

    private async Task ShutdownAsync(Acquisition acquisition)
    {
        Task cancellationTask = Task.CompletedTask;
        await acquisition.CommitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (this.gate)
            {
                if (acquisition.Finished)
                {
                    return;
                }

                acquisition.Tombstoned = true;
                acquisition.Completion.TrySetResult(new ProviderAcquisitionShuttingDown());
            }

            cancellationTask = acquisition.Cancellation.CancelAsync();
        }
        finally
        {
            acquisition.CommitGate.Release();
        }

        await cancellationTask.ConfigureAwait(false);
    }

    private async Task RunAsync(
        IProviderAcquisitionOperation operation,
        Acquisition acquisition,
        ProviderInteractionPermit permit)
    {
        bool IsActive()
        {
            lock (this.gate)
            {
                return !this.disposed && !acquisition.Tombstoned &&
                    !acquisition.Finished && !acquisition.Completion.Task.IsCompleted;
            }
        }

        var context = new ProviderAcquisitionCommitContext(
            IsActive, acquisition.Cancellation.Token, acquisition.CommitGate, permit.BindExecution(IsActive));
        try
        {
            var outcome = await operation.ExecuteAsync(context, acquisition.Cancellation.Token).ConfigureAwait(false);
            if (outcome is ProviderAcquisitionSucceeded succeeded &&
                succeeded.CommittedGeneration != context.CommittedGeneration)
            {
                throw new InvalidOperationException("Provider success requires an authoritative cache commit.");
            }

            lock (this.gate)
            {
                if (!this.disposed && !acquisition.Tombstoned)
                {
                    this.acquisitions.Remove(operation.CompatibilityKey);
                    acquisition.Completion.TrySetResult(outcome);
                }
                else
                {
                    acquisition.Completion.TrySetResult(this.disposed
                        ? new ProviderAcquisitionShuttingDown() : new ProviderAcquisitionCancelled());
                }
            }
        }
        catch (OperationCanceledException) when (acquisition.Cancellation.IsCancellationRequested)
        {
            acquisition.Completion.TrySetResult(new ProviderAcquisitionCancelled());
        }
        catch (Exception exception)
        {
            // Preserve programming failures for the awaiting executor; never turn them into provider success.
            acquisition.Completion.TrySetException(exception);
        }
        finally
        {
            await acquisition.CommitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (this.gate)
                {
                    acquisition.Finished = true;
                    if (this.acquisitions.TryGetValue(operation.CompatibilityKey, out var current) &&
                        ReferenceEquals(current, acquisition))
                    {
                        this.acquisitions.Remove(operation.CompatibilityKey);
                    }
                }

                acquisition.Cancellation.Dispose();
            }
            finally
            {
                acquisition.CommitGate.Release();
            }
        }
    }

    private sealed class Acquisition
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim CommitGate { get; } = new(1, 1);
        public TaskCompletionSource<ProviderAcquisitionOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Participants { get; set; }
        public bool Tombstoned { get; set; }
        public bool Finished { get; set; }
    }
}

using System.Runtime.ExceptionServices;

namespace Phantom.Workspaces.ProcessAcceptance;

public sealed class SplitScenarioPhase : IDisposable
{
    private readonly string name;
    private readonly CancellationToken externalCancellation;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource linked;

    public SplitScenarioPhase(
        string name,
        TimeSpan budget,
        CancellationToken externalCancellation = default,
        TimeProvider? timeProvider = null)
    {
        this.name = name;
        this.externalCancellation = externalCancellation;
        this.deadline = new CancellationTokenSource(budget, timeProvider ?? TimeProvider.System);
        this.linked = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellation, this.deadline.Token);
    }

    public CancellationToken Token => this.linked.Token;
    public string Name => this.name;

    public async Task RunAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            this.Token.ThrowIfCancellationRequested();
            await action(this.Token).WaitAsync(this.Token);
            this.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (
            this.deadline.IsCancellationRequested && !this.externalCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Split scenario phase '{this.name}' exceeded its deadline.", error);
        }
    }

    public void Dispose()
    {
        this.linked.Dispose();
        this.deadline.Dispose();
    }
}

public sealed class SplitScenarioResources(
    TimeSpan? cleanupBudget = null,
    TimeProvider? timeProvider = null)
{
    private readonly Stack<IAsyncDisposable> owners = new();

    public T Add<T>(T owner) where T : IAsyncDisposable
    {
        this.owners.Push(owner);
        return owner;
    }

    public async Task CompleteAsync(Exception? primaryFailure = null)
    {
        var failures = new List<Exception>();
        while (this.owners.TryPop(out var owner))
        {
            try
            {
                using var phase = new SplitScenarioPhase(
                    "cleanup-" + owner.GetType().Name,
                    cleanupBudget ?? TimeSpan.FromSeconds(30),
                    timeProvider: timeProvider);
                await phase.RunAsync(token => owner.DisposeAsync().AsTask().WaitAsync(token));
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        if (failures.Count != 0)
        {
            if (primaryFailure is not null)
                failures.Insert(0, primaryFailure);
            throw new AggregateException("Split scenario operation/cleanup failed.", failures);
        }
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }
}

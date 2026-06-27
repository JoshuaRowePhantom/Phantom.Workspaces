using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ToolResultBrowserViewModelTests
{
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            this.now = this.now.AddSeconds(1);
            return this.now;
        }
    }

    /// <summary>
    /// Completes <see cref="QueryAsync"/> on a thread-pool thread so that a caller which fails to
    /// capture the synchronization context (for example via <c>ConfigureAwait(false)</c>) resumes
    /// off that context.
    /// </summary>
    private sealed class PoolThreadQueryDataAccessLayer : BaseUpdateProcessingDataAccessLayer
    {
        public PoolThreadQueryDataAccessLayer(IDataAccessLayer underlyingDataAccessLayer)
            : base(underlyingDataAccessLayer)
        {
        }

        public override Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
            => Task.Run(() => base.QueryAsync(request, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// A single-threaded synchronization context that pumps posted callbacks on one dedicated
    /// thread, modelling the UI thread for deterministic affinity assertions.
    /// </summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly Thread thread;

        public SingleThreadSynchronizationContext()
        {
            this.thread = new Thread(this.PumpMessages) { IsBackground = true };
            this.thread.Start();
        }

        public int ThreadId => this.thread.ManagedThreadId;

        public override void Post(SendOrPostCallback callback, object? state)
            => this.queue.Add((callback, state));

        public Task Run(Func<Task> operation)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Post(
                async _ =>
                {
                    try
                    {
                        await operation();
                        completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                },
                null);
            return completion.Task;
        }

        private void PumpMessages()
        {
            SynchronizationContext.SetSynchronizationContext(this);
            foreach (var (callback, state) in this.queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose() => this.queue.CompleteAdding();
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    [Fact]
    public async Task RefreshAsync_MutatesHostsOnTheCapturedSynchronizationContext()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new AdvancingTimeProvider());
        await writer.StartAsync(HostName, "vector-indexer", TestContext.Current.CancellationToken);

        var browser = new ToolResultBrowserViewModel(new PoolThreadQueryDataAccessLayer(dataAccessLayer));

        using var context = new SingleThreadSynchronizationContext();
        var mutationThreadIds = new ConcurrentBag<int>();
        browser.Hosts.CollectionChanged += (_, _) => mutationThreadIds.Add(Environment.CurrentManagedThreadId);

        await context.Run(() => browser.RefreshAsync());

        Assert.NotEmpty(mutationThreadIds);
        Assert.All(mutationThreadIds, threadId => Assert.Equal(context.ThreadId, threadId));
    }

    [Fact]
    public async Task RefreshAsync_BuildsHostToolRunTree_WithChildResults()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new AdvancingTimeProvider());

        var run = await writer.StartAsync(HostName, "vector-indexer", TestContext.Current.CancellationToken);
        await writer.StartChildAsync(run, "sub-task", TestContext.Current.CancellationToken);
        await writer.CompleteAsync(run, success: true, cancellationToken: TestContext.Current.CancellationToken);

        var browser = new ToolResultBrowserViewModel(dataAccessLayer);
        await browser.RefreshAsync(TestContext.Current.CancellationToken);

        var host = Assert.Single(browser.Hosts);
        Assert.Equal("computer / this-machine", host.Label);

        var tool = Assert.Single(host.Children);
        Assert.Equal("vector-indexer", tool.Label);

        var runNode = Assert.Single(tool.Children);
        Assert.Equal("succeeded", runNode.Status);
        Assert.Equal("vector-indexer", runNode.ToolName);

        var subTask = Assert.Single(runNode.Children);
        Assert.Equal("sub-task", subTask.Label);

        var childRun = Assert.Single(subTask.Children);
        Assert.Equal("running", childRun.Status);
        Assert.Equal("sub-task", childRun.ToolName);
    }

    [Fact]
    public async Task RefreshAsync_EnumeratesMultipleHosts()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new AdvancingTimeProvider());

        await writer.StartAsync(["computer", "alpha"], "git-workspace-scan", TestContext.Current.CancellationToken);
        await writer.StartAsync(["computer", "beta"], "vector-indexer", TestContext.Current.CancellationToken);

        var browser = new ToolResultBrowserViewModel(dataAccessLayer);
        await browser.RefreshAsync(TestContext.Current.CancellationToken);

        var hostLabels = browser.Hosts.Select(host => host.Label).ToHashSet();
        Assert.Equal(2, browser.Hosts.Count);
        Assert.Contains("computer / alpha", hostLabels);
        Assert.Contains("computer / beta", hostLabels);
    }

    [Fact]
    public async Task RefreshAsync_NoResults_LeavesHostsEmpty()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var browser = new ToolResultBrowserViewModel(dataAccessLayer);

        await browser.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(browser.Hosts);
    }
}

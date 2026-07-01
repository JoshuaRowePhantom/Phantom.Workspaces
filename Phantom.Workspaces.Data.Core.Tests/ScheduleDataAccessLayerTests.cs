using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

#pragma warning disable CS0618
public sealed class ScheduleDataAccessLayerTests
{
    [Fact]
    public async Task AllNineMethods_UseScheduler_WhenCalled()
    {
        var trackingScheduler = new TrackingTaskScheduler();
        var inner = new InMemoryDataAccessLayer();
        var dal = new ScheduleDataAccessLayer(inner, trackingScheduler);

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes = [],
        });
        await dal.GetAsync(new GetRequest { Entities = [] });
        await dal.QueryAsync(new QueryRequest { Clauses = [] });
        await dal.GetHistoryAsync(new GetHistoryRequest { EntityIds = [] });
        await dal.ExportAsync(new ExportRequest());
        await dal.GetChangedEntitiesAsync(new GetChangedEntitiesRequest { EntityIdTimestamps = [] });
        await dal.ProcessQueueAsync(new ProcessQueueRequest { QueueName = "test", Count = 0 });
        await dal.ComputeEmbeddingsAsync(new ComputeEmbeddingsRequest { Entities = [] });
        await dal.UpdateEmbeddingsAsync(new UpdateEmbeddingsRequest { Updates = [] });

        Assert.True(trackingScheduler.InvocationCount >= 9, $"Expected at least 9 scheduler invocations, got {trackingScheduler.InvocationCount}");
    }

    [Fact]
    public async Task AllNineMethods_ForwardResults_ToCallers()
    {
        var inner = new InMemoryDataAccessLayer();
        var dal = new ScheduleDataAccessLayer(inner);

        var updateResult = await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes = [],
        });
        Assert.NotNull(updateResult.EntityResults);

        var getResult = await dal.GetAsync(new GetRequest { Entities = [] });
        Assert.NotNull(getResult.Batches);

        var queryResult = await dal.QueryAsync(new QueryRequest { Clauses = [] });
        Assert.NotNull(queryResult.Batches);

        var historyResult = await dal.GetHistoryAsync(new GetHistoryRequest { EntityIds = [] });
        Assert.NotNull(historyResult.History);

        var exportResult = await dal.ExportAsync(new ExportRequest());
        Assert.NotNull(exportResult.ChangeBatches);

        var changedResult = await dal.GetChangedEntitiesAsync(new GetChangedEntitiesRequest { EntityIdTimestamps = [] });
        Assert.NotNull(changedResult.Entities);

        var queueResult = await dal.ProcessQueueAsync(new ProcessQueueRequest { QueueName = "test", Count = 0 });
        Assert.NotNull(queueResult.Entities);

        var embeddingsResult = await dal.ComputeEmbeddingsAsync(new ComputeEmbeddingsRequest { Entities = [] });
        Assert.NotNull(embeddingsResult.Embeddings);

        var updateEmbeddingsResult = await dal.UpdateEmbeddingsAsync(new UpdateEmbeddingsRequest { Updates = [] });
        Assert.True(updateEmbeddingsResult.Success);
    }

    [Fact]
    public async Task GetAsync_DefaultScheduler_RunsOffCallingThread()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        var capturedThreadIds = new ConcurrentBag<int>();
        var recordingDal = new RecordingThreadIdDataAccessLayer(capturedThreadIds);
        var dal = new ScheduleDataAccessLayer(recordingDal);

        await dal.GetAsync(new GetRequest { Entities = [] });

        var captured = Assert.Single(capturedThreadIds);
        Assert.NotEqual(callingThreadId, captured);
    }

    private sealed class TrackingTaskScheduler : TaskScheduler
    {
        private int invocationCount;

        public int InvocationCount => this.invocationCount;

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref this.invocationCount);
            ThreadPool.QueueUserWorkItem(_ => TryExecuteTask(task));
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    private sealed class RecordingThreadIdDataAccessLayer : IDataAccessLayer
    {
        private readonly ConcurrentBag<int> threadIds;

        public RecordingThreadIdDataAccessLayer(ConcurrentBag<int> threadIds)
        {
            this.threadIds = threadIds;
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            this.threadIds.Add(Environment.CurrentManagedThreadId);
            return Task.FromResult(new GetResult { Batches = [] });
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateResult { EntityResults = [] });

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueryResult { Batches = [] });

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetHistoryResult { History = [] });

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExportResult { ChangeBatches = [], FinalSnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "x") });

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetChangedEntitiesResult { Entities = [] });

        public Task<ProcessQueueResult> ProcessQueueAsync(ProcessQueueRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessQueueResult { Entities = [] });

        public Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(ComputeEmbeddingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComputeEmbeddingsResult { Embeddings = [] });

        public Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(UpdateEmbeddingsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateEmbeddingsResult { Success = true });
    }
}

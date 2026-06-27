using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ToolExecutionResultWriterTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 15, 19, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    private static async Task<JsonElement> ReadEntityAsync(IDataAccessLayer dataAccessLayer, EntityId entityId)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
            Timestamps = [null],
        });
        var snapshot = result.Batches.SelectMany(batch => batch.Entities).Single(entity => entity.EntityId == entityId);
        return snapshot.Data!.Value;
    }

    private static IReadOnlyList<string> FirstName(JsonElement entity)
    {
        return entity.GetProperty("names")[0].EnumerateArray().Select(component => component.GetString()!).ToArray();
    }

    [Fact]
    public async Task StartAsync_CreatesRunningResultAtHostNamePath()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var time = new FixedTimeProvider();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, time);

        var handle = await writer.StartAsync(HostName, "vector-indexer", TestContext.Current.CancellationToken);

        var entity = await ReadEntityAsync(dataAccessLayer, handle.EntityId);
        Assert.Equal("vector-indexer", entity.GetProperty("tool-name").GetString());
        Assert.Equal("running", entity.GetProperty("status").GetString());
        Assert.False(entity.TryGetProperty("end-time", out _));

        var name = FirstName(entity);
        Assert.Equal("computer", name[0]);
        Assert.Equal("this-machine", name[1]);
        Assert.Equal("tool-executions", name[2]);
        Assert.Equal("vector-indexer", name[3]);
        Assert.Equal(5, name.Count);
    }

    [Fact]
    public async Task CompleteAsync_RecordsStatusAndEndTime()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var time = new FixedTimeProvider();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, time);

        var handle = await writer.StartAsync(HostName, "vector-indexer", TestContext.Current.CancellationToken);
        time.Now = time.Now.AddMinutes(2);
        await writer.CompleteAsync(handle, success: true, content: "indexed 5 entities", TestContext.Current.CancellationToken);

        var entity = await ReadEntityAsync(dataAccessLayer, handle.EntityId);
        Assert.Equal("succeeded", entity.GetProperty("status").GetString());
        Assert.True(entity.TryGetProperty("end-time", out _));
        Assert.Equal(
            "indexed 5 entities",
            entity.GetProperty("content").GetProperty("default").GetProperty("text").GetString());
    }

    [Fact]
    public async Task CompleteAsync_WithFailure_RecordsFailedStatus()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new FixedTimeProvider());

        var handle = await writer.StartAsync(HostName, "vector-indexer", TestContext.Current.CancellationToken);
        await writer.CompleteAsync(handle, success: false, cancellationToken: TestContext.Current.CancellationToken);

        var entity = await ReadEntityAsync(dataAccessLayer, handle.EntityId);
        Assert.Equal("failed", entity.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartChildAsync_NestsResultBeneathParent()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new FixedTimeProvider());

        var parent = await writer.StartAsync(HostName, "entity-classifier", TestContext.Current.CancellationToken);
        var child = await writer.StartChildAsync(parent, "classify-entity-42", TestContext.Current.CancellationToken);

        var entity = await ReadEntityAsync(dataAccessLayer, child.EntityId);
        var name = FirstName(entity);

        // The child name path begins with the parent's full name path.
        Assert.True(parent.NameComponents.SequenceEqual(name.Take(parent.NameComponents.Count)));
        Assert.Equal("classify-entity-42", name[parent.NameComponents.Count]);
    }
}

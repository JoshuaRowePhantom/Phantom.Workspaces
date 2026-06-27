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

public sealed class VectorIndexerToolTests
{
    private static async Task<EntityId> AddEntityAsync(IDataAccessLayer dataAccessLayer, string nameLeaf, string text)
    {
        var guid = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["entity", "note"],
              "names": [["notes","{{nameLeaf}}"]],
              "content": { "text": {{JsonSerializer.Serialize(text)}} }
            }
            """);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(guid),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
        return new EntityId(guid);
    }

    private static WorkspaceToolExecutionContext Context(IDataAccessLayer dataAccessLayer) =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            """{ "entity-types": ["entity", "tool"], "tool-type": "vector-indexer" }""");

    [Fact]
    public async Task Run_DrainsTheVectorIndexQueue()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await AddEntityAsync(dataAccessLayer, "a", "alpha");
        await AddEntityAsync(dataAccessLayer, "b", "beta");
        await AddEntityAsync(dataAccessLayer, "c", "gamma");

        var tool = new VectorIndexerTool(batchSize: 2);
        await tool.ExecuteAsync(Context(dataAccessLayer));

        // After indexing, the queue head has advanced past every entity, so a fresh read is empty.
        var drained = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = VectorIndexerTool.QueueName,
            Count = 10,
        }, TestContext.Current.CancellationToken);
        Assert.Empty(drained.Entities);
    }

    [Fact]
    public async Task Run_StoresEmbeddingsThatDriveVectorSearch()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var apple = await AddEntityAsync(dataAccessLayer, "apple", "red apple fruit");
        await AddEntityAsync(dataAccessLayer, "ocean", "blue ocean water");

        var tool = new VectorIndexerTool(batchSize: 10);
        await tool.ExecuteAsync(Context(dataAccessLayer));

        var query = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("vector"),
                    Clause = new EntityVectorQueryClause
                    {
                        VectorQueryIdentifier = new QueryClauseIdentifier("vector"),
                        QueryText = "red apple fruit",
                        NumberOfCandidates = 1,
                    },
                },
            ],
        };
        var result = await dataAccessLayer.QueryAsync(query, TestContext.Current.CancellationToken);
        var match = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(apple, match.EntityId);
    }

    [Fact]
    public async Task Run_IsIdempotent_WhenNothingChanged()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await AddEntityAsync(dataAccessLayer, "a", "alpha");

        var tool = new VectorIndexerTool(batchSize: 10);
        await tool.ExecuteAsync(Context(dataAccessLayer));

        // A second run finds nothing new to index and completes without error.
        await tool.ExecuteAsync(Context(dataAccessLayer));

        var drained = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = VectorIndexerTool.QueueName,
            Count = 10,
        }, TestContext.Current.CancellationToken);
        Assert.Empty(drained.Entities);
    }
}

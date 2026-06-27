using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class EntityClassifierToolTests
{
    private sealed record Classification(EntityId EntityId, string Prompt, string? BeforeText, string? AfterText);

    private sealed class RecordingRunner : IEntityClassifierAgentRunner
    {
        private readonly Func<EntityClassificationRequest, Task>? onRun;
        public List<EntityClassificationRequest> Requests { get; } = [];
        public List<Classification> Classifications { get; } = [];

        public RecordingRunner(Func<EntityClassificationRequest, Task>? onRun = null)
        {
            this.onRun = onRun;
        }

        public async Task RunAsync(EntityClassificationRequest request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            if (this.onRun is not null)
            {
                await this.onRun(request);
            }
        }

        public Task OnClassifiedAsync(EntityId entityId, EntitySnapshot beforeSnapshot, EntitySnapshot? afterSnapshot, CancellationToken cancellationToken)
        {
            this.Classifications.Add(new Classification(entityId, string.Empty, ReadText(beforeSnapshot.Data), ReadText(afterSnapshot?.Data)));
            return Task.CompletedTask;
        }

        private static string? ReadText(JsonElement? data) =>
            data is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("content", out var content)
            && content.TryGetProperty("text", out var text)
                ? text.GetString()
                : null;
    }

    private static async Task<EntityId> AddEntityAsync(IDataAccessLayer dataAccessLayer, string nameLeaf, string text, string entityType = "note")
    {
        var guid = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["entity", "{{entityType}}"],
              "names": [["notes","{{nameLeaf}}"]],
              "content": { "text": {{JsonSerializer.Serialize(text)}} }
            }
            """);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes = [new EntityChange { EntityId = new EntityId(guid), ConcurrencyTag = null, Data = document.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
        return new EntityId(guid);
    }

    private static async Task ReplaceTextAsync(IDataAccessLayer dataAccessLayer, EntityId entityId, string newText)
    {
        var current = (await dataAccessLayer.GetAsync(new GetRequest { Entities = [new GetEntityRequest { EntityId = entityId }], Timestamps = [null] }))
            .Batches.SelectMany(b => b.Entities).Single(e => e.EntityId == entityId);
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity", "note"],
              "names": [["notes","x"]],
              "content": { "text": {{JsonSerializer.Serialize(newText)}} }
            }
            """);
        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "modify" } },
            Changes = [new EntityChange { EntityId = entityId, ConcurrencyTag = current.ConcurrencyTag, Data = document.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        });
    }

    private static WorkspaceToolExecutionContext Context(IDataAccessLayer dataAccessLayer, string prompt = "Classify this entity.") =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["entity", "tool"], "tool-type": "entity-classifier", "classifier-prompt": {{JsonSerializer.Serialize(prompt)}} }""");

    [Fact]
    public async Task Run_InvokesRunnerOncePerEntity_AndDrainsQueue()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await AddEntityAsync(dataAccessLayer, "a", "alpha");
        await AddEntityAsync(dataAccessLayer, "b", "beta");
        var runner = new RecordingRunner();

        await new EntityClassifierTool(runner, batchSize: 1).ExecuteAsync(Context(dataAccessLayer));

        Assert.Equal(2, runner.Requests.Count);
        var drained = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest { QueueName = EntityClassifierTool.QueueName, Count = 10 }, TestContext.Current.CancellationToken);
        Assert.Empty(drained.Entities);
    }

    [Fact]
    public async Task Run_AssemblesPromptInOrder()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        // Seed an entity-type entity so the "all entity types" section is populated.
        await AddEntityAsync(dataAccessLayer, "type-note", "ignored", entityType: "entity-type");
        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "type" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(Guid.NewGuid()),
                    ConcurrencyTag = null,
                    Data = JsonDocument.Parse("""{ "entity-types": ["entity", "entity-type"], "names": [["entity-types","note"]] }""").RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, TestContext.Current.CancellationToken);
        await AddEntityAsync(dataAccessLayer, "target", "the entity body text");
        var runner = new RecordingRunner();

        await new EntityClassifierTool(runner).ExecuteAsync(Context(dataAccessLayer, "PROMPT-HEADER"));

        var prompt = runner.Requests
            .Select(r => r.Prompt)
            .First(p => p.Contains("the entity body text", StringComparison.Ordinal));
        var headerIndex = prompt.IndexOf("PROMPT-HEADER", StringComparison.Ordinal);
        var allTypesIndex = prompt.IndexOf("# All entity types", StringComparison.Ordinal);
        var typesIndex = prompt.IndexOf("# Entity types", StringComparison.Ordinal);
        var contentIndex = prompt.IndexOf("# Entity content", StringComparison.Ordinal);
        var relationshipsIndex = prompt.IndexOf("# Relationships", StringComparison.Ordinal);

        Assert.True(headerIndex >= 0 && headerIndex < allTypesIndex);
        Assert.True(allTypesIndex < typesIndex);
        Assert.True(typesIndex < contentIndex);
        Assert.True(contentIndex < relationshipsIndex);
        Assert.Contains("note", prompt[allTypesIndex..typesIndex]);
        Assert.Contains("the entity body text", prompt);
    }

    [Fact]
    public async Task Run_IncludesInterestInstructions_AfterAllEntityTypes_AndBeforeEntityTypes()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        // Seed an interest-type entity (as the schema populator would).
        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "interest" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(Guid.NewGuid()),
                    ConcurrencyTag = null,
                    Data = JsonDocument.Parse(
                        """
                        {
                          "entity-types": ["entity", "interest-type","relationship-type","entity-type","note"],
                          "names": [["entity-types","not-interesting"]],
                          "applied": { "indicator": "x", "description": "Not interesting", "actionText": "Mark not interesting" },
                          "notApplied": { "indicator": "", "description": "Interesting", "actionText": "Clear" }
                        }
                        """).RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, TestContext.Current.CancellationToken);
        await AddEntityAsync(dataAccessLayer, "target", "the entity body text");
        var runner = new RecordingRunner();

        await new EntityClassifierTool(runner).ExecuteAsync(Context(dataAccessLayer));

        var prompt = runner.Requests
            .Select(r => r.Prompt)
            .First(p => p.Contains("the entity body text", StringComparison.Ordinal));
        var allTypesIndex = prompt.IndexOf("# All entity types", StringComparison.Ordinal);
        var interestsIndex = prompt.IndexOf("# Interests", StringComparison.Ordinal);
        var typesIndex = prompt.IndexOf("# Entity types", StringComparison.Ordinal);

        Assert.True(allTypesIndex >= 0);
        Assert.True(allTypesIndex < interestsIndex, "Interests should come after the entity-type list.");
        Assert.True(interestsIndex < typesIndex, "Interests should come before the entity's own types.");

        var interestSection = prompt[interestsIndex..typesIndex];
        Assert.Contains("not-interesting", interestSection, StringComparison.Ordinal);
        Assert.Contains("Not interesting", interestSection, StringComparison.Ordinal);
        Assert.Contains("'note'", interestSection, StringComparison.Ordinal);
        Assert.Contains("not-interesting", interestSection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RetrievesBeforeAndAfterSnapshots()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var entityId = await AddEntityAsync(dataAccessLayer, "a", "before-text");

        // The runner modifies the entity, so the after snapshot differs from the before snapshot.
        var runner = new RecordingRunner(async request =>
        {
            if (request.EntityId == entityId)
            {
                await ReplaceTextAsync(request.DataAccessLayer, request.EntityId, "after-text");
            }
        });

        await new EntityClassifierTool(runner).ExecuteAsync(Context(dataAccessLayer));

        var classification = Assert.Single(runner.Classifications);
        Assert.Equal("before-text", classification.BeforeText);
        Assert.Equal("after-text", classification.AfterText);
    }
}

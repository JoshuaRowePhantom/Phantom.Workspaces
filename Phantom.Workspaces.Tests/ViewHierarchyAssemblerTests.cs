using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ViewHierarchyAssemblerTests
{
    [AvaloniaFact]
    public async Task AssembleAsync_NestsRelatedMembersUnderDedupedContextualParent()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Entity-type-views declaring the traversals: tasks traverse 'related' members; notes resolve
        // their contextual parent via the 'reference' (target) relationship.
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","task"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"], "relationship-role-names": ["entities"], "max-depth": 1 }
              ]
            }
            """);
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","note"]],
              "parent-hierarchy-relationships": [
                { "relationship-type-ids": ["reference"], "relationship-role-names": ["target"], "max-depth": 1 }
              ]
            }
            """);

        var taskId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]], "display-name": { "default": "Do thing" } }""");
        var parentId = await SeedNoteAsync(dataAccessLayer, "folder", "Folder");
        var member1Id = await SeedNoteAsync(dataAccessLayer, "m1", "Member one");
        var member2Id = await SeedNoteAsync(dataAccessLayer, "m2", "Member two");

        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related","relationship"],
              "names": [["relationships","r-task-members"]],
              "participants": { "entities": ["{{taskId.Value}}", "{{member1Id.Value}}", "{{member2Id.Value}}"] }
            }
            """);
        await SeedReferenceAsync(dataAccessLayer, member1Id, parentId);
        await SeedReferenceAsync(dataAccessLayer, member2Id, parentId);

        var roots = (await broker.GetEntitiesAsync([taskId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        // task (root) -> folder (deduped contextual parent) -> member1, member2.
        var taskNode = Assert.Single(hierarchy);
        Assert.Equal(taskId, taskNode.Entity.EntityId);
        var parentNode = Assert.Single(taskNode.Children);
        Assert.Equal(parentId, parentNode.Entity.EntityId);
        var memberIds = parentNode.Children.Select(child => child.Entity.EntityId).ToHashSet();
        Assert.Equal([member1Id, member2Id], memberIds.OrderBy(id => id.Value).ToHashSet());
    }

    [AvaloniaFact]
    public async Task AssembleAsync_WithoutEntityTypeViews_ProducesFlatChildlessNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var taskId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","flat"]], "display-name": { "default": "Flat" } }""");

        var roots = (await broker.GetEntitiesAsync([taskId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var node = Assert.Single(hierarchy);
        Assert.Equal(taskId, node.Entity.EntityId);
        Assert.Empty(node.Children);
    }

    private static Task<EntityId> SeedNoteAsync(IDataAccessLayer dataAccessLayer, string name, string displayName)
        => SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "note"],
              "names": [["notes","{{name}}"]],
              "display-name": { "default": {{JsonSerializer.Serialize(displayName)}} },
              "content": { "mime-type": "text/markdown", "content": { "text": {{JsonSerializer.Serialize(displayName)}} } }
            }
            """);

    private static Task SeedReferenceAsync(IDataAccessLayer dataAccessLayer, EntityId source, EntityId target)
        => SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "reference","relationship"],
              "names": [["relationships","ref-{{source.Value}}-{{target.Value}}"]],
              "participants": { "source": "{{source.Value}}", "target": "{{target.Value}}" }
            }
            """);

    private static async Task<EntityId> SeedAsync(IDataAccessLayer dataAccessLayer, string json)
    {
        var guid = Guid.NewGuid();
        using var template = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", guid);
            foreach (var property in template.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
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

        var failure = result.EntityResults.FirstOrDefault(static r => r.UpdateState == UpdateState.Failed);
        Assert.True(
            failure is null,
            failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static e => e.Message)));
        return new EntityId(guid);
    }
}

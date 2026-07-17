using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// End-to-end tests for the inbox and workstreams views: they populate an in-memory repository,
/// execute the view-definition queries (interest participation: tasks assigned to the current user;
/// entities actionable for the current user), and populate the standard view-model layer
/// (<see cref="ViewEntityViewModel"/>) from the results.
/// </summary>
public sealed class InterestViewQueryTests
{
    [AvaloniaFact]
    public async Task WorkstreamsView_AssemblesHierarchy_OfAssignedTasksRelatedMembersAndContextualParents()
    {
        var dataAccessLayer = (await EntityRepository.CreateAsync(new UnknownRepositorySource())).DataAccessLayer;
        var alice = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "user"], "names": [["users","alice","alice"]] }""");
        var bob = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "user"], "names": [["users","bob","bob"]] }""");

        var assignedTask = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","assigned"]], "display-name": { "default": "Alice Task" } }""");
        var unassignedTask = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","unassigned"]], "display-name": { "default": "Bob Task" } }""");
        await SeedAssignedToAsync(dataAccessLayer, assignedTask, alice);
        await SeedAssignedToAsync(dataAccessLayer, unassignedTask, bob);

        // Two related members share a contextual parent, reached via the relationship type the member
        // entity type's entity-type-view designates in its parent-hierarchy-relationships (here the
        // registered 'reference' type, traversing to the 'target' role). A member whose entity-type-view
        // has no parent-hierarchy-relationships (or none matching) has no contextual parent.
        var contextualParent = await SeedNoteAsync(dataAccessLayer, ["notes", "parent"], "Parent");
        var relatedOne = await SeedNoteAsync(dataAccessLayer, ["notes", "one"], "Member One");
        var relatedTwo = await SeedNoteAsync(dataAccessLayer, ["notes", "two"], "Member Two");
        var unrelated = await SeedNoteAsync(dataAccessLayer, ["notes", "x"], "Unrelated");

        await SeedRelatedAsync(dataAccessLayer, assignedTask, relatedOne);
        await SeedRelatedAsync(dataAccessLayer, assignedTask, relatedTwo);
        await SeedRelatedAsync(dataAccessLayer, unassignedTask, unrelated);

        // The 'note' entity-type-view designates 'reference' (target role) as its contextual-parent
        // relationship; both members reference the same parent.
        await SeedNoteParentHierarchyViewAsync(dataAccessLayer);
        await SeedReferenceAsync(dataAccessLayer, source: relatedOne, target: contextualParent);
        await SeedReferenceAsync(dataAccessLayer, source: relatedTwo, target: contextualParent);

        var roots = await AssembleWorkstreamsAsync(dataAccessLayer, alice);
        var viewModels = PopulateHierarchyViewModels(roots);
        var byId = viewModels.ToDictionary(vm => vm.Entity.EntityId);

        // 2. Tasks assigned to the user are present, at the root.
        var rootIds = roots.Select(node => node.Entity.EntityId).ToHashSet();
        Assert.Contains(assignedTask, rootIds);
        Assert.Equal(0, byId[assignedTask].IndentLevel);

        // 3. Contextual parents of related entities are present, nested under the task.
        Assert.True(
            byId.ContainsKey(contextualParent),
            $"Contextual parent missing. roots={roots.Count}; "
            + $"rootChildren=[{string.Join(",", roots.SelectMany(r => r.Children).Select(c => c.Entity.EntityId.Value))}]; "
            + $"allIds=[{string.Join(",", byId.Keys.Select(k => k.Value))}]; "
            + $"expectedParent={contextualParent.Value}; relatedOne={relatedOne.Value}");
        Assert.Equal(1, byId[contextualParent].IndentLevel);

        // 1. Entities related to assigned tasks are present, nested under their contextual parent.
        Assert.True(byId.ContainsKey(relatedOne), "Related member one missing.");
        Assert.True(byId.ContainsKey(relatedTwo), "Related member two missing.");
        Assert.Equal(2, byId[relatedOne].IndentLevel);
        Assert.Equal(2, byId[relatedTwo].IndentLevel);

        // 6. The shared contextual parent groups both children and appears exactly once.
        var parentNodes = Flatten(roots).Where(node => node.Entity.EntityId == contextualParent).ToArray();
        var parentNode = Assert.Single(parentNodes);
        var parentChildIds = parentNode.Children.Select(child => child.Entity.EntityId).ToHashSet();
        Assert.Contains(relatedOne, parentChildIds);
        Assert.Contains(relatedTwo, parentChildIds);

        // 4. Tasks not assigned to the user are not present.
        Assert.DoesNotContain(unassignedTask, byId.Keys);
        // 5. Entities not related to assigned tasks are not present.
        Assert.DoesNotContain(unrelated, byId.Keys);
    }

    /// <summary>
    /// Assembles the workstreams hierarchy for a user: assigned tasks (roots), the entities related to
    /// each task grouped under their contextual (name-hierarchy) parent (deduplicated so a shared
    /// parent appears once), with members lacking a contextual parent attached directly to the task.
    /// </summary>
    private async Task<IReadOnlyList<WorkstreamNode>> AssembleWorkstreamsAsync(IDataAccessLayer dataAccessLayer, EntityId user)
    {
        var assignedTasks = await ExecuteAsync(dataAccessLayer, TargetsOfInterestForUser("assigned-to", user));
        var roots = new List<WorkstreamNode>();
        foreach (var task in assignedTasks)
        {
            var taskNode = new WorkstreamNode(task);
            var parentNodesById = new Dictionary<EntityId, WorkstreamNode>();
            foreach (var member in await GetRelatedMembersAsync(dataAccessLayer, task.EntityId))
            {
                var parent = await GetContextualParentAsync(dataAccessLayer, member);
                if (parent is null)
                {
                    taskNode.Children.Add(new WorkstreamNode(member));
                    continue;
                }

                if (!parentNodesById.TryGetValue(parent.EntityId, out var parentNode))
                {
                    parentNodesById[parent.EntityId] = parentNode = new WorkstreamNode(parent);
                    taskNode.Children.Add(parentNode);
                }

                parentNode.Children.Add(new WorkstreamNode(member));
            }

            roots.Add(taskNode);
        }

        return roots;
    }

    private async Task<IReadOnlyList<EntitySnapshot>> GetRelatedMembersAsync(IDataAccessLayer dataAccessLayer, EntityId taskId)
    {
        var participants = await ExecuteAsync(dataAccessLayer, new EntityParticipationQueryClause
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["related"]),
            ParticipationRoleNames = new RoleNameSet(["entities"]),
            MustHave = new EntityParticipationRequirement
            {
                ParticipationRoleNames = new RoleNameSet(["entities"]),
                Clause = new EntityFieldQueryClause
                {
                    FieldPath = new FieldPath("entity-id"),
                    ComparisonOperator = FieldComparisonOperator.Equals,
                    Value = JsonSerializer.SerializeToElement(taskId.Value.ToString()),
                },
            },
        });
        return participants.Where(snapshot => snapshot.EntityId != taskId).ToArray();
    }

    private static async Task<EntitySnapshot?> GetContextualParentAsync(IDataAccessLayer dataAccessLayer, EntitySnapshot entity)
    {
        foreach (var entityType in ReadEntityTypes(entity.Data))
        {
            var view = await GetEntityByNameAsync(dataAccessLayer, ["entity-type-views", entityType]);
            if (view?.Data is not { } viewData
                || !viewData.TryGetProperty("parent-hierarchy-relationships", out var traversals)
                || traversals.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var traversal in traversals.EnumerateArray())
            {
                var relationshipTypeIds = ReadStringArray(traversal, "relationship-type-ids");
                if (relationshipTypeIds.Length == 0)
                {
                    continue;
                }

                var parentRoleNames = ReadStringArray(traversal, "relationship-role-names");
                var parents = await ExecuteAsync(dataAccessLayer, new EntityParticipationQueryClause
                {
                    RelationshipTypeNames = new RelationshipTypeNameSet(relationshipTypeIds),
                    ParticipationRoleNames = parentRoleNames.Length > 0 ? new RoleNameSet(parentRoleNames) : null,
                    MustHave = new EntityParticipationRequirement
                    {
                        Clause = new EntityFieldQueryClause
                        {
                            FieldPath = new FieldPath("entity-id"),
                            ComparisonOperator = FieldComparisonOperator.Equals,
                            Value = JsonSerializer.SerializeToElement(entity.EntityId.Value.ToString()),
                        },
                    },
                });

                var parent = parents.FirstOrDefault(candidate => candidate.EntityId != entity.EntityId);
                if (parent is not null)
                {
                    return parent;
                }
            }
        }

        return null;
    }

    private static async Task<EntitySnapshot?> GetEntityByNameAsync(IDataAccessLayer dataAccessLayer, string[] name)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityName = new EntityName(name) }],
            Timestamps = [null],
        });
        return result.Batches.SelectMany(batch => batch.Entities).FirstOrDefault(snapshot => snapshot.Data is not null);
    }

    private static IEnumerable<string> ReadEntityTypes(JsonElement? data)
    {
        if (data is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("entity-types", out var types)
            && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var type in types.EnumerateArray())
            {
                if (type.ValueKind == JsonValueKind.String && type.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        return [];
    }

    private List<ViewEntityViewModel> PopulateHierarchyViewModels(IReadOnlyList<WorkstreamNode> roots)
    {
        var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var shortcutManager = new ShortcutManager();
        var viewModels = new List<ViewEntityViewModel>();

        void Visit(WorkstreamNode node, int indentLevel)
        {
            viewModels.Add(new ViewEntityViewModel(
                new SubscribedEntityViewModel(node.Entity, deleteEntityAsync: null),
                mainWindowViewModel,
                shortcutManager,
                indentLevel));
            foreach (var child in node.Children)
            {
                Visit(child, indentLevel + 1);
            }
        }

        foreach (var root in roots)
        {
            Visit(root, 0);
        }

        return viewModels;
    }

    private static IEnumerable<WorkstreamNode> Flatten(IEnumerable<WorkstreamNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var descendant in Flatten(node.Children))
            {
                yield return descendant;
            }
        }
    }

    private sealed class WorkstreamNode
    {
        public WorkstreamNode(EntitySnapshot entity)
        {
            this.Entity = entity;
        }

        public EntitySnapshot Entity { get; }

        public List<WorkstreamNode> Children { get; } = [];
    }

    private static Task<EntityId> SeedNoteAsync(IDataAccessLayer dataAccessLayer, string[] name, string displayName)
    {
        var namesJson = JsonSerializer.Serialize(new[] { name });
        return SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "note"],
              "names": {{namesJson}},
              "display-name": { "default": {{JsonSerializer.Serialize(displayName)}} },
              "content": { "mime-type": "text/markdown", "content": { "text": {{JsonSerializer.Serialize(displayName)}} } }
            }
            """);
    }

    private static Task SeedRelatedAsync(IDataAccessLayer dataAccessLayer, EntityId first, EntityId second)
        => SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "related","relationship"],
              "names": [["relationships","related-{{first.Value}}-{{second.Value}}"]],
              "participants": { "entities": ["{{first.Value}}", "{{second.Value}}"] }
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

    private static Task SeedNoteParentHierarchyViewAsync(IDataAccessLayer dataAccessLayer)
        => SeedAsync(
            dataAccessLayer,
            """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","note"]],
              "parent-hierarchy-relationships": [
                { "relationship-type-ids": ["reference"], "relationship-role-names": ["target"], "max-depth": 1 }
              ]
            }
            """);


    [AvaloniaFact]
    public async Task InboxView_PopulatesViewModels_WithCurrentUsersActionableItems()
    {
        var dataAccessLayer = (await EntityRepository.CreateAsync(new UnknownRepositorySource())).DataAccessLayer;
        var alice = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "user"], "names": [["users","alice","alice"]] }""");
        var bob = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "user"], "names": [["users","bob","bob"]] }""");
        // The inbox shows entities of any type, not just tasks.
        var actionableNote = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "note"], "names": [["notes","n"]], "display-name": { "default": "Review PR" }, "content": { "mime-type": "text/markdown", "content": { "text": "Review the pull request" } } }""");
        var actionableTask = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]], "display-name": { "default": "Do thing" } }""");
        var bobsItem = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "note"], "names": [["notes","m"]], "display-name": { "default": "Bob note" }, "content": { "mime-type": "text/markdown", "content": { "text": "Bob's note" } } }""");
        await SeedActionableAsync(dataAccessLayer, actionableNote, alice);
        await SeedActionableAsync(dataAccessLayer, actionableTask, alice);
        await SeedActionableAsync(dataAccessLayer, bobsItem, bob);

        var results = await ExecuteAsync(dataAccessLayer, TargetsOfInterestForUser("actionable", alice));

        var viewModels = PopulateViewModels(results);
        var ids = viewModels.Select(vm => vm.Entity.EntityId).ToHashSet();
        Assert.Contains(actionableNote, ids);
        Assert.Contains(actionableTask, ids);
        Assert.DoesNotContain(bobsItem, ids);
        Assert.Equal(2, ids.Count);
    }

    /// <summary>
    /// The view-definition query for "entities that are the target of interest <paramref name="interestType"/>
    /// whose user participant is the current user" - the inbox uses 'actionable', the workstreams view
    /// uses 'assigned-to'. The current user is bound here (the query DAL will bind it from the session).
    /// </summary>
    private static EntityParticipationQueryClause TargetsOfInterestForUser(string interestType, EntityId userId)
        => new()
        {
            RelationshipTypeNames = new RelationshipTypeNameSet([interestType]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
            MustHave = new EntityParticipationRequirement
            {
                ParticipationRoleNames = new RoleNameSet(["user"]),
                Clause = new EntityFieldQueryClause
                {
                    FieldPath = new FieldPath("entity-id"),
                    ComparisonOperator = FieldComparisonOperator.Equals,
                    Value = JsonSerializer.SerializeToElement(userId.Value.ToString()),
                },
            },
        };

    private List<ViewEntityViewModel> PopulateViewModels(IReadOnlyList<EntitySnapshot> snapshots)
    {
        var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var shortcutManager = new ShortcutManager();
        return snapshots
            .Select(snapshot => new ViewEntityViewModel(
                new SubscribedEntityViewModel(snapshot, deleteEntityAsync: null),
                mainWindowViewModel,
                shortcutManager,
                indentLevel: 0))
            .ToList();
    }

    private static async Task<IReadOnlyList<EntitySnapshot>> ExecuteAsync(
        IDataAccessLayer dataAccessLayer,
        QueryClause clause)
    {
        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("view"),
                    Clause = clause,
                },
            ],
        });
        return result.Batches.SelectMany(batch => batch.Entities).Cast<EntitySnapshot>().ToArray();
    }

    private static Task SeedAssignedToAsync(IDataAccessLayer dataAccessLayer, EntityId target, EntityId user)
        => SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "assigned-to","relationship"],
              "names": [["relationships","assigned-{{target.Value}}"]],
              "participants": { "target": "{{target.Value}}", "user": "{{user.Value}}" }
            }
            """);

    private static Task SeedActionableAsync(IDataAccessLayer dataAccessLayer, EntityId target, EntityId user)
        => SeedAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "actionable","relationship"],
              "names": [["relationships","actionable-{{target.Value}}"]],
              "participants": { "target": "{{target.Value}}", "user": "{{user.Value}}" }
            }
            """);

    private static async Task<EntityId> SeedAsync(IDataAccessLayer dataAccessLayer, string json)
    {
        var guid = Guid.NewGuid();
        using var template = JsonDocument.Parse(json);
        using var stream = new System.IO.MemoryStream();
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

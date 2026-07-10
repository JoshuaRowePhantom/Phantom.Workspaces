using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ViewHierarchyAssemblerTests
{
    [PhantomAvaloniaFact]
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
        Assert.Equal(taskId, taskNode.Entity!.EntityId);
        var parentNode = Assert.Single(taskNode.Children);
        Assert.Equal(parentId, parentNode.Entity!.EntityId);
        var memberIds = parentNode.Children.Select(child => child.Entity!.EntityId).ToHashSet();
        Assert.Equal([member1Id, member2Id], memberIds.OrderBy(id => id.Value).ToHashSet());
    }

    [PhantomAvaloniaFact]
    public async Task AssembleAsync_WithoutEntityTypeViews_ProducesFlatChildlessNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var taskId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","flat"]], "display-name": { "default": "Flat" } }""");

        var roots = (await broker.GetEntitiesAsync([taskId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var node = Assert.Single(hierarchy);
        Assert.Equal(taskId, node.Entity!.EntityId);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void WorkspaceEntityTypeViewJson_HasTraverseRelationships()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        const string resourceName = "Phantom.Workspaces.Data.JsonEntities.entity_type_views.workspace-entity-type-view.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Assert.True(
            root.TryGetProperty("traverse-relationships", out var traversals),
            "workspace-entity-type-view.json must contain a 'traverse-relationships' property");

        Assert.Equal(JsonValueKind.Array, traversals.ValueKind);
        Assert.NotEmpty(traversals.EnumerateArray());

        var relatedEntry = traversals.EnumerateArray().FirstOrDefault(
            static t => t.TryGetProperty("relationship-type-ids", out var ids)
                && ids.EnumerateArray().Any(static id => id.GetString() == "related"));

        Assert.True(
            relatedEntry.ValueKind != JsonValueKind.Undefined,
            "traverse-relationships must include an entry with relationship-type-ids containing 'related'");
    }

    [PhantomAvaloniaFact]
    public async Task AssembleAsync_WithCollapsedDisposition_SetsIsExpandedFalseOnRootNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","workspace"]],
              "traversed-entity-display-disposition": "collapsed",
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var workspaceId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces","ws-collapsed"]],
              "display-name": { "default": "Collapsed Workspace" },
              "regions": []
            }
            """);
        var noteId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "note"],
              "names": [["notes","n-collapsed"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "note" } }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships","ws-note-collapsed-rel"]],
              "participants": { "entities": ["{{workspaceId.Value}}", "{{noteId.Value}}"] }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([workspaceId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var workspaceNode = Assert.Single(hierarchy);
        Assert.Equal(workspaceId, workspaceNode.Entity!.EntityId);
        // The node should have the child in the hierarchy but be collapsed.
        Assert.Single(workspaceNode.Children);
        Assert.False(workspaceNode.IsExpanded);
    }

    [PhantomAvaloniaFact]
    public async Task AssembleAsync_WithoutDisposition_DefaultsToIsExpandedTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Use "task" (no built-in entity-type-view) so the seeded view without
        // traversed-entity-display-disposition is the only one found for this type.
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","task"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var taskId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task"],
              "names": [["tasks","task-default-expanded"]],
              "display-name": { "default": "Default Expanded Task" }
            }
            """);
        var noteId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "note"],
              "names": [["notes","n-default-expanded"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "note" } }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships","task-note-expanded-rel"]],
              "participants": { "entities": ["{{taskId.Value}}", "{{noteId.Value}}"] }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([taskId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var taskNode = Assert.Single(hierarchy);
        Assert.True(taskNode.IsExpanded);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_WorkspaceWithRelatedEntity_RendersEntityAsChild()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","workspace"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var workspaceId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces","ws1"]],
              "display-name": { "default": "My Workspace" },
              "regions": [{ "region-id": "center", "title": "Center", "dock": "center", "tabs": [], "size": 1 }]
            }
            """);
        var noteId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "note"],
              "names": [["notes","n1"]],
              "display-name": { "default": "My Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "My Note" } }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships","ws-note-rel"]],
              "participants": { "entities": ["{{workspaceId.Value}}", "{{noteId.Value}}"] }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([workspaceId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var workspaceNode = Assert.Single(hierarchy);
        Assert.Equal(workspaceId, workspaceNode.Entity!.EntityId);
        var childNode = Assert.Single(workspaceNode.Children);
        Assert.Equal(noteId, childNode.Entity!.EntityId);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_WorkspaceWithNoRelatedEntities_RendersFlat()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","workspace"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var workspaceId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces","ws-flat"]],
              "display-name": { "default": "Empty Workspace" },
              "regions": [{ "region-id": "center", "title": "Center", "dock": "center", "tabs": [], "size": 1 }]
            }
            """);

        var roots = (await broker.GetEntitiesAsync([workspaceId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var workspaceNode = Assert.Single(hierarchy);
        Assert.Equal(workspaceId, workspaceNode.Entity!.EntityId);
        Assert.Empty(workspaceNode.Children);
    }

    [Fact]
    public void RepositoryEntityTypeViewJson_HasTraverseRelationshipsWithRelated()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        const string resourceName = "Phantom.Workspaces.Data.JsonEntities.entity_type_views.repository-entity-type-view.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Assert.True(
            root.TryGetProperty("traverse-relationships", out var traversals),
            "repository-entity-type-view.json must contain a 'traverse-relationships' property");

        Assert.Equal(JsonValueKind.Array, traversals.ValueKind);
        Assert.NotEmpty(traversals.EnumerateArray());

        var relatedEntry = traversals.EnumerateArray().FirstOrDefault(
            static t => t.TryGetProperty("relationship-type-ids", out var ids)
                && ids.EnumerateArray().Any(static id => id.GetString() == "related"));

        Assert.True(
            relatedEntry.ValueKind != JsonValueKind.Undefined,
            "traverse-relationships must include an entry with relationship-type-ids containing 'related'");
    }

    [Fact]
    public void AncestorSynthesis_ProducesOneRelationshipObjectPerChildEntity()
    {
        var entities = new[]
        {
            MakeSyntheticEntity(new EntityId(), ["github", "microsoft", "vscode", "pull-requests", "1234"]),
            MakeSyntheticEntity(new EntityId(), ["github", "microsoft", "vscode", "pull-requests", "1235"]),
            MakeSyntheticEntity(new EntityId(), ["github", "JRowe", "Phantom.Workspaces", "pull-requests", "42"]),
        };

        var results = AncestorSynthesizer.Synthesize(entities, namePrefixLength: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Select(static r => string.Join("\0", r.NamePrefix)).Distinct().Count());
        var vscodePrefixed = results.Where(static r => r.NamePrefix.SequenceEqual(["github", "microsoft", "vscode"])).ToList();
        Assert.Equal(2, vscodePrefixed.Count);
        var phantomPrefixed = results.Where(static r => r.NamePrefix.SequenceEqual(["github", "JRowe", "Phantom.Workspaces"])).ToList();
        Assert.Single(phantomPrefixed);
    }

    [Fact]
    public void AncestorSynthesis_NeverWritesAnyEntityToStore()
    {
        var entities = new[]
        {
            MakeSyntheticEntity(new EntityId(), ["ns", "org", "repo", "item"]),
        };

        // Synthesis is pure: it produces no side-effects and touches no storage.
        // Verify by confirming the results are purely in-memory objects with no entity-id of their own.
        var results = AncestorSynthesizer.Synthesize(entities, namePrefixLength: 3);

        Assert.Single(results);
        // AncestorRelationshipObject has no entity-id; it only carries ChildEntityId.
        Assert.Equal(entities[0].EntityId, results[0].ChildEntityId);
    }

    [Fact]
    public void AncestorSynthesis_RelationshipObjectHasNoEntityInEntityTypes()
    {
        var entities = new[]
        {
            MakeSyntheticEntity(new EntityId(), ["ns", "org", "repo", "item"]),
        };

        var results = AncestorSynthesizer.Synthesize(entities, namePrefixLength: 3);

        var obj = Assert.Single(results);
        Assert.Contains("relationship", AncestorRelationshipObject.EntityTypes);
        Assert.Contains("ancestor", AncestorRelationshipObject.EntityTypes);
        Assert.DoesNotContain("entity", AncestorRelationshipObject.EntityTypes);
    }

    [Fact]
    public void AncestorSynthesis_ShortNameEntity_ProducesNoAncestorRelationship()
    {
        var entities = new[]
        {
            MakeSyntheticEntity(new EntityId(), ["ns", "item"]),              // length 2 == prefix-length
            MakeSyntheticEntity(new EntityId(), ["ns"]),                      // length 1 < prefix-length
            MakeSyntheticEntity(new EntityId(), ["ns", "org", "repo"]),       // length 3 == prefix-length
            MakeSyntheticEntity(new EntityId(), ["ns", "org", "repo", "x"]), // length 4 > prefix-length → gets one
        };

        var results = AncestorSynthesizer.Synthesize(entities, namePrefixLength: 3);

        var single = Assert.Single(results);
        Assert.Equal(entities[3].EntityId, single.ChildEntityId);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_UsesAncestorRelationshipAsGroupNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Declare an entity-type-view for "workspace" that traverses "task" entities via ancestor grouping.
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","workspace"]],
              "traverse-relationships": [
                { "relationship-type": "ancestor", "entity-type-names": ["task"], "name-prefix-length": 3 }
              ]
            }
            """);

        var workspaceId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces","ws1"]],
              "display-name": { "default": "My Workspace" },
              "regions": [{ "region-id": "center", "title": "Center", "dock": "center", "tabs": [], "size": 1 }]
            }
            """);

        // Two task entities sharing the same 3-segment prefix, using multi-segment names.
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task"],
              "names": [["github", "org", "repo", "issues", "1"]],
              "display-name": { "default": "Issue #1" }
            }
            """);
        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task"],
              "names": [["github", "org", "repo", "issues", "2"]],
              "display-name": { "default": "Issue #2" }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([workspaceId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        // workspace (root) → one ancestor group node for ["github","org","repo"] → Issue #1 + Issue #2
        var workspaceNode = Assert.Single(hierarchy);
        Assert.Equal(workspaceId, workspaceNode.Entity!.EntityId);

        var groupNode = Assert.Single(workspaceNode.Children);
        Assert.True(groupNode.IsAncestorGroup, "Child of workspace should be an ancestor group node");
        Assert.Equal("repo", groupNode.DisplayName);
        Assert.Equal(2, groupNode.Children.Count);
        Assert.All(groupNode.Children, static child => Assert.False(child.IsAncestorGroup));
    }

    [PhantomAvaloniaFact]
    public async Task PullRequestsView_ShowsPullRequestsGroupedUnderRepository()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "repository"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var repoAId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "repo-a"]],
              "display-name": { "default": "repo-a" }
            }
            """);
        var repoBId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "repo-b"]],
              "display-name": { "default": "repo-b" }
            }
            """);

        var pr1Id = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "owner", "repo-a", "1"]],
              "display-name": { "default": "PR 1" }
            }
            """);
        var pr2Id = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "owner", "repo-a", "2"]],
              "display-name": { "default": "PR 2" }
            }
            """);
        var pr3Id = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "owner", "repo-b", "3"]],
              "display-name": { "default": "PR 3" }
            }
            """);

        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "repo-a-pr1"]],
              "participants": { "entities": ["{{repoAId.Value}}", "{{pr1Id.Value}}"] }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "repo-a-pr2"]],
              "participants": { "entities": ["{{repoAId.Value}}", "{{pr2Id.Value}}"] }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "repo-b-pr3"]],
              "participants": { "entities": ["{{repoBId.Value}}", "{{pr3Id.Value}}"] }
            }
            """);

        var repoARoots = (await broker.GetEntitiesAsync([repoAId], ct)).ToArray();
        var repoBRoots = (await broker.GetEntitiesAsync([repoBId], ct)).ToArray();
        var assembler = new ViewHierarchyAssembler(broker);

        var repoAHierarchy = await assembler.AssembleAsync(repoARoots, ct);
        var repoBHierarchy = await assembler.AssembleAsync(repoBRoots, ct);

        var repoANode = Assert.Single(repoAHierarchy);
        Assert.Equal(repoAId, repoANode.Entity!.EntityId);
        Assert.Equal(2, repoANode.Children.Count);
        Assert.Contains(repoANode.Children, c => c.Entity!.EntityId == pr1Id);
        Assert.Contains(repoANode.Children, c => c.Entity!.EntityId == pr2Id);

        var repoBNode = Assert.Single(repoBHierarchy);
        Assert.Equal(repoBId, repoBNode.Entity!.EntityId);
        var child = Assert.Single(repoBNode.Children);
        Assert.Equal(pr3Id, child.Entity!.EntityId);
    }

    [PhantomAvaloniaFact]
    public async Task PullRequestsView_ShowsEmptyRepositoryNode_WhenNoPrsExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "repository"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var repoId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "empty-repo"]],
              "display-name": { "default": "empty-repo" }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([repoId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var repoNode = Assert.Single(hierarchy);
        Assert.Equal(repoId, repoNode.Entity!.EntityId);
        Assert.Empty(repoNode.Children);
    }

    [PhantomAvaloniaFact]
    public async Task PullRequestsView_ShowsPullRequestsFromMultipleProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "repository"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var githubRepoId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "org", "gh-repo"]],
              "display-name": { "default": "gh-repo" }
            }
            """);
        var gitRepoId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository"],
              "names": [["git-repositories", "https://example.com/git-repo.git"]],
              "display-name": { "default": "git-repo" }
            }
            """);

        var githubPrId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "org", "gh-repo", "42"]],
              "display-name": { "default": "GitHub PR #42" }
            }
            """);
        var gitPrId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request"],
              "names": [["git-pull-requests", "https://example.com/git-repo.git", "7"]],
              "display-name": { "default": "Git PR #7" }
            }
            """);

        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "gh-repo-pr42"]],
              "participants": { "entities": ["{{githubRepoId.Value}}", "{{githubPrId.Value}}"] }
            }
            """);
        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "git-repo-pr7"]],
              "participants": { "entities": ["{{gitRepoId.Value}}", "{{gitPrId.Value}}"] }
            }
            """);

        var assembler = new ViewHierarchyAssembler(broker);

        var githubRoots = (await broker.GetEntitiesAsync([githubRepoId], ct)).ToArray();
        var githubHierarchy = await assembler.AssembleAsync(githubRoots, ct);
        var githubNode = Assert.Single(githubHierarchy);
        var githubChild = Assert.Single(githubNode.Children);
        Assert.Equal(githubPrId, githubChild.Entity!.EntityId);

        var gitRoots = (await broker.GetEntitiesAsync([gitRepoId], ct)).ToArray();
        var gitHierarchy = await assembler.AssembleAsync(gitRoots, ct);
        var gitNode = Assert.Single(gitHierarchy);
        var gitChild = Assert.Single(gitNode.Children);
        Assert.Equal(gitPrId, gitChild.Entity!.EntityId);
    }

    [Fact]
    public void PullRequestEntityTypeView_GroupByParent_UsesRepositoryField()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        const string resourceName = "Phantom.Workspaces.Data.JsonEntities.entity_type_views.pull-request-entity-type-view.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        Assert.True(
            root.TryGetProperty("group-by-parent", out var groupByParent),
            "pull-request-entity-type-view.json must contain a 'group-by-parent' property");

        Assert.Equal(JsonValueKind.Object, groupByParent.ValueKind);

        Assert.True(
            groupByParent.TryGetProperty("field-path", out var fieldPath),
            "group-by-parent must contain 'field-path'");
        Assert.Equal(JsonValueKind.Array, fieldPath.ValueKind);
        var fieldPathValues = fieldPath.EnumerateArray().Select(static e => e.GetString()!).ToArray();
        Assert.Equal(["repository"], fieldPathValues);

        Assert.True(
            groupByParent.TryGetProperty("parent-entity-type-names", out var parentEntityTypeNames),
            "group-by-parent must contain 'parent-entity-type-names'");
        Assert.Equal(JsonValueKind.Array, parentEntityTypeNames.ValueKind);
        var parentEntityTypeNameValues = parentEntityTypeNames.EnumerateArray().Select(static e => e.GetString()!).ToArray();
        Assert.Contains("repository", parentEntityTypeNameValues, StringComparer.Ordinal);
    }

    [PhantomAvaloniaFact]
    public async Task PullRequestsView_GroupsPullRequestsByRepositoryField()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "pull-request"]],
              "group-by-parent": {
                "source": "field",
                "field-path": ["repository"],
                "parent-entity-type-names": ["repository"]
              }
            }
            """);

        var repoAId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "repo-a"]],
              "display-name": { "default": "repo-a" }
            }
            """);
        var repoBId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "repo-b"]],
              "display-name": { "default": "repo-b" }
            }
            """);

        var pr1Id = await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "owner", "repo-a", "1"]],
              "display-name": { "default": "PR 1 in repo-a" },
              "repository": "{{repoAId.Value}}"
            }
            """);
        var pr2Id = await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request", "github-pull-request"],
              "names": [["github-pull-requests", "owner", "repo-b", "2"]],
              "display-name": { "default": "PR 2 in repo-b" },
              "repository": "{{repoBId.Value}}"
            }
            """);

        var query = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("pr-type"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["pull-request"]),
                    },
                },
            ],
        };
        var prRoots = (await broker.GetEntitiesAsync(query, ct)).ToList();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(prRoots, ct);

        Assert.Equal(2, hierarchy.Count);
        var repoANode = hierarchy.SingleOrDefault(n => n.Entity?.EntityId == repoAId);
        var repoBNode = hierarchy.SingleOrDefault(n => n.Entity?.EntityId == repoBId);
        Assert.NotNull(repoANode);
        Assert.NotNull(repoBNode);
        var repoAChild = Assert.Single(repoANode.Children);
        Assert.Equal(pr1Id, repoAChild.Entity!.EntityId);
        var repoBChild = Assert.Single(repoBNode.Children);
        Assert.Equal(pr2Id, repoBChild.Entity!.EntityId);
    }

    [PhantomAvaloniaFact]
    public async Task PullRequestsView_PullRequestWithNoRepository_RendersUngrouped()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "pull-request"]],
              "group-by-parent": {
                "source": "field",
                "field-path": ["repository"],
                "parent-entity-type-names": ["repository"]
              }
            }
            """);

        var prId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request"],
              "names": [["pull-requests", "orphan-pr"]],
              "display-name": { "default": "Orphan PR" }
            }
            """);

        var query = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("pr-type"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["pull-request"]),
                    },
                },
            ],
        };
        var prRoots = (await broker.GetEntitiesAsync(query, ct)).ToList();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(prRoots, ct);

        var prNode = Assert.Single(hierarchy);
        Assert.Equal(prId, prNode.Entity!.EntityId);
        Assert.Empty(prNode.Children);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_Roots_SortedByDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var charlieId = await SeedNoteAsync(dataAccessLayer, "charlie", "Charlie");
        var alphaId = await SeedNoteAsync(dataAccessLayer, "alpha", "Alpha");
        var bravoId = await SeedNoteAsync(dataAccessLayer, "bravo", "Bravo");

        var roots = (await broker.GetEntitiesAsync([charlieId, alphaId, bravoId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var sortedNames = hierarchy.Select(n => n.Entity!.Snapshot.Data!.Value.GetProperty("display-name").GetProperty("default").GetString()!).ToArray();
        Assert.Equal(["Alpha", "Bravo", "Charlie"], sortedNames);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_Roots_SortIsCaseInsensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var gammaId = await SeedNoteAsync(dataAccessLayer, "gamma", "gamma");
        var BetaId = await SeedNoteAsync(dataAccessLayer, "Beta", "Beta");
        var alphaId = await SeedNoteAsync(dataAccessLayer, "alpha", "alpha");

        var roots = (await broker.GetEntitiesAsync([gammaId, BetaId, alphaId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var sortedNames = hierarchy.Select(n => n.Entity!.Snapshot.Data!.Value.GetProperty("display-name").GetProperty("default").GetString()!).ToArray();
        Assert.Equal(["alpha", "Beta", "gamma"], sortedNames);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_Roots_FallsBackToNameSegments_WhenNoDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var zId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "note"], "names": [["notes","z"]], "content": { "mime-type": "text/plain", "content": { "text": "z" } } }""");
        var aId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "note"], "names": [["notes","a"]], "content": { "mime-type": "text/plain", "content": { "text": "a" } } }""");

        var roots = (await broker.GetEntitiesAsync([zId, aId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var sortedIds = hierarchy.Select(n => n.Entity!.EntityId).ToArray();
        Assert.Equal([aId, zId], sortedIds);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_TraversedChildren_SortedByDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","task"]],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"], "relationship-role-names": ["entities"], "max-depth": 1 }
              ]
            }
            """);

        var taskId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]], "display-name": { "default": "Task" } }""");
        var zId = await SeedNoteAsync(dataAccessLayer, "z", "Zebra");
        var aId = await SeedNoteAsync(dataAccessLayer, "a", "Apple");
        var mId = await SeedNoteAsync(dataAccessLayer, "m", "Mango");

        await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "related","relationship"],
              "names": [["relationships","r-task-notes"]],
              "participants": { "entities": ["{{taskId.Value}}", "{{zId.Value}}", "{{aId.Value}}", "{{mId.Value}}"] }
            }
            """);

        var roots = (await broker.GetEntitiesAsync([taskId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var taskNode = Assert.Single(hierarchy);
        var childNames = taskNode.Children.Select(c => c.Entity!.Snapshot.Data!.Value.GetProperty("display-name").GetProperty("default").GetString()!).ToArray();
        Assert.Equal(["Apple", "Mango", "Zebra"], childNames);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_GroupByParent_ChildrenSortedByDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","pull-request"]],
              "group-by-parent": { 
                "source": "field",
                "field-path": ["repository"],
                "parent-entity-type-names": ["repository"]
              }
            }
            """);

        var repoId = await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "external", "repository", "git-repository", "github-repository"],
              "names": [["github-repositories", "owner", "test-repo"]],
              "display-name": { "default": "Test Repo" }
            }
            """);

        var prZ = await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request"],
              "names": [["pull-requests", "pr-z"]],
              "display-name": { "default": "Zulu PR" },
              "repository": "{{repoId.Value}}"
            }
            """);

        var prA = await SeedAsync(dataAccessLayer, $$"""
            {
              "entity-types": ["entity", "task", "external", "pull-request", "git-pull-request"],
              "names": [["pull-requests", "pr-a"]],
              "display-name": { "default": "Alpha PR" },
              "repository": "{{repoId.Value}}"
            }
            """);

        var leaves = (await broker.GetEntitiesAsync([prZ, prA], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(leaves, ct);

        var repoNode = Assert.Single(hierarchy);
        Assert.Equal(repoId, repoNode.Entity!.EntityId);
        var childNames = repoNode.Children.Select(c => c.Entity!.Snapshot.Data!.Value.GetProperty("display-name").GetProperty("default").GetString()!).ToArray();
        Assert.Equal(["Alpha PR", "Zulu PR"], childNames);
    }

    [PhantomAvaloniaFact]
    public async Task ViewHierarchyAssembler_AncestorGroups_SortedByDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        await SeedAsync(dataAccessLayer, """
            {
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views","workspace"]],
              "traverse-relationships": [
                { "relationship-type": "ancestor", "entity-type-names": ["workspace"], "name-prefix-length": 2 }
              ]
            }
            """);

        var rootId = await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","root"]], "display-name": { "default": "Root" }, "regions": [] }""");
        await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","zoo","item"]], "display-name": { "default": "Zoo Item" }, "regions": [] }""");
        await SeedAsync(dataAccessLayer, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","apple","item"]], "display-name": { "default": "Apple Item" }, "regions": [] }""");

        var roots = (await broker.GetEntitiesAsync([rootId], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(roots, ct);

        var rootNode = Assert.Single(hierarchy);
        var groupNames = rootNode.Children
            .Where(c => c.IsAncestorGroup)
            .Select(c => c.DisplayName!)
            .ToArray();
        
        // Should contain at least our two test groups, sorted
        Assert.Contains("apple", groupNames);
        Assert.Contains("zoo", groupNames);
        var appleIndex = Array.IndexOf(groupNames, "apple");
        var zooIndex = Array.IndexOf(groupNames, "zoo");
        Assert.True(appleIndex < zooIndex, "apple should come before zoo");
    }

    private static SubscribedEntityViewModel MakeSyntheticEntity(EntityId entityId, string[] nameParts)
    {
        var json = $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity", "pull-request"],
              "names": [{{System.Text.Json.JsonSerializer.Serialize(nameParts)}}],
              "display-name": { "default": "{{nameParts[^1]}}" }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var snapshot = new EntitySnapshot
        {
            EntityId = entityId,
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "0"),
            Data = doc.RootElement.Clone(),
            Relationships = [],
        };
        return new SubscribedEntityViewModel(snapshot);
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
        using var template = JsonDocument.Parse(json);

        // If the template declares names, look up any entity that already carries the primary name
        // (e.g. entity-type-views pre-seeded by SchemaPopulator).  Reusing the existing entity-id
        // and concurrency-tag turns this into an update rather than a create, so the store never
        // holds two entities with the same name and GetEntityTypeViewAsync always returns exactly
        // one result — eliminating the non-deterministic FirstOrDefault() pick that caused flakiness.
        EntityId? existingId = null;
        ConcurrencyTag? existingConcurrencyTag = null;
        if (template.RootElement.TryGetProperty("names", out var namesEl)
            && namesEl.ValueKind == JsonValueKind.Array)
        {
            var firstNameEl = namesEl.EnumerateArray().FirstOrDefault();
            if (firstNameEl.ValueKind == JsonValueKind.Array)
            {
                var components = firstNameEl.EnumerateArray()
                    .Where(static e => e.ValueKind == JsonValueKind.String)
                    .Select(static e => e.GetString()!)
                    .ToArray();
                if (components.Length > 0)
                {
                    var lookupResult = await dataAccessLayer.GetAsync(new GetRequest
                    {
                        Entities = [new GetEntityRequest { EntityName = new EntityName(components) }],
                    });
                    var existing = lookupResult.Batches.SelectMany(static b => b.Entities).FirstOrDefault();
                    if (existing is not null)
                    {
                        existingId = existing.EntityId;
                        existingConcurrencyTag = existing.ConcurrencyTag;
                    }
                }
            }
        }

        var guid = existingId?.Value ?? Guid.NewGuid();
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
                    ConcurrencyTag = existingConcurrencyTag,
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

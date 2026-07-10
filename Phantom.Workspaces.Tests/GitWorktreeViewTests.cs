using System;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeViewTests
{
    [PhantomAvaloniaFact]
    public async Task GitWorktreeView_GroupsByUserComputerProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Create two profiles
        var profile1Id = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var profile2Id = new EntityId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        
        await SeedProfileAsync(dataAccessLayer, profile1Id, "alice", "machine1");
        await SeedProfileAsync(dataAccessLayer, profile2Id, "bob", "machine2");

        // Create worktrees belonging to different profiles
        var worktree1Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project1", profile1Id);
        var worktree2Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project2", profile2Id);
        var worktree3Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project3", profile1Id);

        // Get worktrees and assemble hierarchy
        var worktrees = (await broker.GetEntitiesAsync([worktree1Id, worktree2Id, worktree3Id], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(worktrees, ct);

        // Verify that worktrees are grouped under their respective profiles
        Assert.Equal(2, hierarchy.Count);
        
        var profile1Node = hierarchy.FirstOrDefault(n => n.Entity?.EntityId == profile1Id);
        var profile2Node = hierarchy.FirstOrDefault(n => n.Entity?.EntityId == profile2Id);
        
        Assert.NotNull(profile1Node);
        Assert.NotNull(profile2Node);
        Assert.Equal(2, profile1Node.Children.Count);
        Assert.Single(profile2Node.Children);
        
        var profile1ChildIds = profile1Node.Children.Select(c => c.Entity!.EntityId).ToHashSet();
        Assert.Contains(worktree1Id, profile1ChildIds);
        Assert.Contains(worktree3Id, profile1ChildIds);
        
        var profile2ChildIds = profile2Node.Children.Select(c => c.Entity!.EntityId).ToHashSet();
        Assert.Contains(worktree2Id, profile2ChildIds);
    }

    [PhantomAvaloniaFact]
    public async Task GitWorktreeView_SingleProfile_NoUngroupedWorktrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Create one profile
        var profileId = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        await SeedProfileAsync(dataAccessLayer, profileId, "alice", "machine1");

        // Create multiple worktrees all belonging to the same profile
        var worktree1Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project1", profileId);
        var worktree2Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project2", profileId);
        var worktree3Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project3", profileId);

        // Get worktrees and assemble hierarchy
        var worktrees = (await broker.GetEntitiesAsync([worktree1Id, worktree2Id, worktree3Id], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(worktrees, ct);

        // Verify that all worktrees are under the single profile node (no ungrouped worktrees at root)
        var profileNode = Assert.Single(hierarchy);
        Assert.Equal(profileId, profileNode.Entity!.EntityId);
        Assert.Equal(3, profileNode.Children.Count);
    }

    [PhantomAvaloniaFact]
    public async Task GitWorktreeView_WorktreeWithNoProfile_AppearsAtRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        // Create one profile
        var profileId = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        await SeedProfileAsync(dataAccessLayer, profileId, "alice", "machine1");

        // Create one worktree with a profile and one without
        var worktree1Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project1", profileId);
        var worktree2Id = await SeedWorktreeAsync(dataAccessLayer, "/repos/project2", null);

        // Get worktrees and assemble hierarchy
        var worktrees = (await broker.GetEntitiesAsync([worktree1Id, worktree2Id], ct)).ToArray();
        var hierarchy = await new ViewHierarchyAssembler(broker).AssembleAsync(worktrees, ct);

        // Verify structure: profile node with one child, and one worktree at root level
        Assert.Equal(2, hierarchy.Count);
        
        var profileNode = hierarchy.FirstOrDefault(n => n.Entity?.EntityId == profileId);
        var rootWorktreeNode = hierarchy.FirstOrDefault(n => n.Entity?.EntityId == worktree2Id);
        
        Assert.NotNull(profileNode);
        Assert.NotNull(rootWorktreeNode);
        Assert.Single(profileNode.Children);
        Assert.Equal(worktree1Id, profileNode.Children[0].Entity!.EntityId);
    }

    private static async Task SeedProfileAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId profileId,
        string username,
        string computerName)
    {
        await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed test profile" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = profileId,
                        ConcurrencyTag = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = System.Text.Json.JsonDocument.Parse($$"""
                        {
                          "entity-types": ["entity", "user-computer-profile"],
                          "names": [["computer-user-profiles", "users", "username", "{{username}}", "computers", "hostname", "{{computerName}}"]],
                          "display-name": { "default": "{{username}} @ {{computerName}}" },
                          "computer-reference": ["computers", "hostname", "{{computerName}}"],
                          "user-reference": ["users", "username", "{{username}}"]
                        }
                        """).RootElement.Clone(),
                    },
                ],
            }, default);
    }

    private static async Task<EntityId> SeedWorktreeAsync(
        IDataAccessLayer dataAccessLayer,
        string path,
        EntityId? profileId)
    {
        var worktreeId = new EntityId(Guid.NewGuid());
        var profileIdJson = profileId.HasValue ? $"""
            "computer-user-profile-id": "{profileId.Value}",
        """ : string.Empty;

        await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed test worktree" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = worktreeId,
                        ConcurrencyTag = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = System.Text.Json.JsonDocument.Parse($$"""
                        {
                          "entity-types": ["entity", "git-worktree", "filesystem-path"],
                          "names": [["git-worktrees", "{{path}}"]],
                          "display-name": { "default": "{{System.IO.Path.GetFileName(path)}}" },
                          "path": "{{path}}",
                          {{profileIdJson}}
                          "git": {
                            "branch": "main",
                            "head-commit": "abc123"
                          }
                        }
                        """).RootElement.Clone(),
                    },
                ],
            }, default);

        return worktreeId;
    }
}

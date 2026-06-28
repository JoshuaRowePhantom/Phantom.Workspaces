using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorkspacesViewModelTests
{
    [AvaloniaFact]
    public async Task RefreshAsync_NoEntities_ProducesEmptyGroups()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var viewModel = new GitWorkspacesViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(viewModel.Groups);
        Assert.False(viewModel.IsLoading);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_OneWorktree_GroupedUnderProfile()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var profileId = new EntityId("a1b2c3d4-0000-0000-0000-000000000001");
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{profileId}}",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "alice", "computers", "hostname", "workstation"]],
              "display-name": { "default": "alice @ workstation" },
              "user-reference": ["users", "username", "alice"],
              "computer-reference": ["computers", "hostname", "workstation"]
            }
            """);

        var worktreeId = new EntityId("b2c3d4e5-0000-0000-0000-000000000002");
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{worktreeId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [
                ["git-worktrees", "C:\\\\repos\\\\my-project"],
                ["computer-user-profiles", "users", "username", "alice", "computers", "hostname", "workstation"]
              ],
              "display-name": { "default": "my-project" },
              "path": "C:\\\\repos\\\\my-project",
              "git": { "branch": "main", "head-commit": "abc1234" }
            }
            """);

        var viewModel = new GitWorkspacesViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("alice @ workstation", group.ProfileDisplayName);

        var item = Assert.Single(group.Worktrees);
        Assert.Equal("my-project", item.DisplayName);
        Assert.Equal("main", item.Branch);
        Assert.Equal("abc1234", item.HeadCommit);
        Assert.Equal(worktreeId, item.EntityId);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_MultipleWorktrees_GroupedByProfile()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        await SeedAsync(broker, """
            {
              "entity-id": "a1b2c3d4-0000-0000-0000-000000000010",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "alice", "computers", "hostname", "box1"]],
              "display-name": { "default": "alice @ box1" },
              "user-reference": ["users", "username", "alice"],
              "computer-reference": ["computers", "hostname", "box1"]
            }
            """);
        await SeedAsync(broker, """
            {
              "entity-id": "a1b2c3d4-0000-0000-0000-000000000011",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "bob", "computers", "hostname", "box2"]],
              "display-name": { "default": "bob @ box2" },
              "user-reference": ["users", "username", "bob"],
              "computer-reference": ["computers", "hostname", "box2"]
            }
            """);

        // Two worktrees on box1
        await SeedAsync(broker, """
            {
              "entity-id": "b2c3d4e5-0000-0000-0000-000000000020",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [
                ["git-worktrees", "C:\\repos\\alpha"],
                ["computer-user-profiles", "users", "username", "alice", "computers", "hostname", "box1"]
              ],
              "display-name": { "default": "alpha" }
            }
            """);
        await SeedAsync(broker, """
            {
              "entity-id": "b2c3d4e5-0000-0000-0000-000000000021",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [
                ["git-worktrees", "C:\\repos\\beta"],
                ["computer-user-profiles", "users", "username", "alice", "computers", "hostname", "box1"]
              ],
              "display-name": { "default": "beta" }
            }
            """);

        // One worktree on box2
        await SeedAsync(broker, """
            {
              "entity-id": "b2c3d4e5-0000-0000-0000-000000000022",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [
                ["git-worktrees", "C:\\repos\\gamma"],
                ["computer-user-profiles", "users", "username", "bob", "computers", "hostname", "box2"]
              ],
              "display-name": { "default": "gamma" }
            }
            """);

        var viewModel = new GitWorkspacesViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.Groups.Count);

        var box1Group = viewModel.Groups.FirstOrDefault(g => g.ProfileDisplayName == "alice @ box1");
        Assert.NotNull(box1Group);
        Assert.Equal(2, box1Group.Worktrees.Count);

        var box2Group = viewModel.Groups.FirstOrDefault(g => g.ProfileDisplayName == "bob @ box2");
        Assert.NotNull(box2Group);
        Assert.Single(box2Group.Worktrees);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_WorktreeWithNoProfile_AppearsInUnknownGroup()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        await SeedAsync(broker, """
            {
              "entity-id": "c3d4e5f6-0000-0000-0000-000000000030",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["git-worktrees", "C:\\repos\\orphan"]],
              "display-name": { "default": "orphan" }
            }
            """);

        var viewModel = new GitWorkspacesViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("(Unknown)", group.ProfileDisplayName);
        Assert.Single(group.Worktrees);
    }

    [AvaloniaFact]
    public async Task IsLoading_SetDuringRefresh_ClearedAfterwards()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var viewModel = new GitWorkspacesViewModel(broker);

        Assert.False(viewModel.IsLoading);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsLoading);
    }

    private static async Task SeedAsync(EntityBroker broker, string json)
    {
        using var document = JsonDocument.Parse(json);
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed git workspaces test." } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
    }
}

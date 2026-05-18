using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacePaneViewModelTests
{
    [Fact]
    public void HasNoRegions_IsTrueWhenEmpty_AndFalseWhenRegionsExist()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());

        Assert.True(pane.HasNoRegions);
        Assert.False(pane.HasRegions);

        pane.SetRegions(
        [
            new WorkspaceRegionViewModel
            {
                Id = "editor-center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            },
        ]);

        Assert.False(pane.HasNoRegions);
        Assert.True(pane.HasRegions);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["workspace"],
              "display-name": { "default": "Workspace" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("11111111-1111-1111-1111-111111111111"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }
}

using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledTasksViewModelTests
{
    [AvaloniaFact]
    public async Task RefreshAsync_LoadsScheduledTasks_WithResolvedParticipantNames()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("f7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("0b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("a1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("a638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "folder"], "names": [["tools","git-workspace-scan"]], "display-name": { "default": "Git Workspace Scan" } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "folder"], "names": [["schedule","every-five-minutes"]], "display-name": { "default": "Every five minutes" } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","jrowe-daemon"]], "display-name": { "default": "jrowe @ DAEMON" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["relationship","{{relationshipId}}"]],
              "note": "Scheduling Git Workspace Scan every five minutes.",
              "participants": {
                "tool": "{{toolId}}",
                "schedule": ["{{scheduleId}}"],
                "target": ["{{targetId}}"]
              }
            }
            """);

        var viewModel = new ScheduledTasksViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);
        Assert.Equal("Git Workspace Scan", task.ToolDisplayName);
        Assert.Equal("Every five minutes", task.ScheduleDisplayName);
        Assert.Equal("jrowe @ DAEMON", task.TargetDisplayName);
        Assert.True(task.HasNote);
        Assert.True(viewModel.HasScheduledTasks);
    }

    private static async Task SeedAsync(
        EntityBroker broker,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed scheduled tasks test." } },
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

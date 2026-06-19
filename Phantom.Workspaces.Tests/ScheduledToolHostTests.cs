using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolHostTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero); // Wednesday.

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    private sealed class RecordingTool : IWorkspaceTool
    {
        public int RunCount { get; private set; }

        public IReadOnlyList<EntitySnapshot> LastParticipants { get; private set; } = [];

        public EntityId CurrentComputerEntityId { get; private set; }

        public EntityId CurrentUserEntityId { get; private set; }

        public EntityId CurrentProfileEntityId { get; private set; }

        public string ToolType => "stub";

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            this.RunCount++;
            this.LastParticipants = context.Participants;
            this.CurrentComputerEntityId = context.CurrentComputerEntity.EntityId;
            this.CurrentUserEntityId = context.CurrentUserEntity.EntityId;
            this.CurrentProfileEntityId = context.CurrentComputerUserProfileEntity.EntityId;
            return Task.FromResult(new WorkspaceToolExecutionResult());
        }
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    private static async Task AddEntityAsync(IDataAccessLayer dataAccessLayer, Guid id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(id),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }

    private static async Task SeedScenarioAsync(
        IDataAccessLayer dataAccessLayer,
        Guid hostId,
        Guid userId,
        Guid computerId,
        Guid toolId,
        Guid scheduleId,
        Guid relationshipId,
        string scheduleRepeatJson)
    {
        await AddEntityAsync(dataAccessLayer, userId,
            $$"""{ "entity-id": "{{userId}}", "entity-types": ["user"], "names": [["users","username","test-user"]] }""");
        await AddEntityAsync(dataAccessLayer, computerId,
            $$"""{ "entity-id": "{{computerId}}", "entity-types": ["computer"], "names": [["computers","hostname","this-machine"]] }""");
        await AddEntityAsync(dataAccessLayer, hostId,
            $$"""{ "entity-id": "{{hostId}}", "entity-types": ["user-computer-profile"], "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]], "user-reference": ["users","username","test-user"], "computer-reference": ["computers","hostname","this-machine"] }""");
        await AddEntityAsync(dataAccessLayer, toolId,
            $$"""{ "entity-id": "{{toolId}}", "entity-types": ["tool"], "names": [["tools","stub"]], "tool-type": "stub" }""");
        await AddEntityAsync(dataAccessLayer, scheduleId,
            $$"""{ "entity-id": "{{scheduleId}}", "entity-types": ["schedule"], "names": [["schedule","test"]], "repeat": {{scheduleRepeatJson}} }""");
        await AddEntityAsync(dataAccessLayer, relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["tool-relationship"],
              "names": [["tool-relationships","test"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] }
            }
            """);
    }

    [Fact]
    public async Task RunDueTools_RunsDueTool_AndRecordsResult()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Interval schedule that has never run -> due.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName);

        Assert.Equal(1, ranCount);
        Assert.Equal(1, tool.RunCount);
        Assert.Contains(tool.LastParticipants, participant => participant.EntityId == new EntityId(hostId));
        Assert.Equal(new EntityId(computerId), tool.CurrentComputerEntityId);
        Assert.Equal(new EntityId(userId), tool.CurrentUserEntityId);
        Assert.Equal(new EntityId(hostId), tool.CurrentProfileEntityId);

        // A succeeded tool-execution-result was recorded under the host.
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("succeeded", resultEntity.GetProperty("status").GetString());
        Assert.Equal("stub", resultEntity.GetProperty("tool-name").GetString());
    }

    [Fact]
    public async Task RunDueTools_DoesNotRunToolWhoseScheduleIsNotDue()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Interval schedule restricted to Mondays; "now" is a Wednesday -> not an allowed day.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": ["monday"], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName);

        Assert.Equal(0, ranCount);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_IgnoresRelationshipsNotTargetingHost()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, otherHostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        // The relationship targets otherHostId, not hostId.
        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName);

        Assert.Equal(0, ranCount);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_AfterRunningOnce_DoesNotRerunWhenIntervalNotElapsed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Hourly interval; the fixed clock does not advance between runs.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "01:00:00Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        Assert.Equal(1, await host.RunDueToolsAsync(new EntityId(hostId), HostName));
        Assert.Equal(0, await host.RunDueToolsAsync(new EntityId(hostId), HostName));
        Assert.Equal(1, tool.RunCount);
    }

    private static async Task<IReadOnlyList<JsonElement>> QueryByTypeAsync(IDataAccessLayer dataAccessLayer, string entityType)
    {
        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("by-type"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet([entityType]) },
                },
            ],
        });
        return result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.Data!.Value).ToArray();
    }
}

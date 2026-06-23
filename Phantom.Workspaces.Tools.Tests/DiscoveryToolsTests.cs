using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class DiscoveryToolsTests
{
    [Fact]
    public async Task ComputerDiscoveryTool_CreatesComputerEntityDeterministically()
    {
        var provider = new FixedExecutionContextProvider(
            computerName: "test-computer",
            userName: "test-user",
            operatingSystemName: "windows",
            homeDirectoryPath: @"C:\Users\test-user");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(dataAccessLayer, provider);
        var tool = new ComputerDiscoveryTool(provider);

        await tool.ExecuteAsync(context);

        var discovered = await GetEntityByNameAsync(dataAccessLayer, new EntityName("computers", "hostname", "test-computer"));
        Assert.NotNull(discovered);
        var historyCountAfterFirstRun = await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId);
        await tool.ExecuteAsync(context);
        Assert.Equal(historyCountAfterFirstRun, await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId));
        Assert.Contains("\"os\":\"windows\"", discovered.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserDiscoveryTool_CreatesUserEntityDeterministically()
    {
        var provider = new FixedExecutionContextProvider(
            computerName: "test-computer",
            userName: "test-user",
            operatingSystemName: "windows",
            homeDirectoryPath: @"C:\Users\test-user");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(dataAccessLayer, provider);
        var tool = new UserDiscoveryTool(provider);

        await tool.ExecuteAsync(context);

        var discovered = await GetEntityByNameAsync(dataAccessLayer, new EntityName("users", "username", "test-user"));
        Assert.NotNull(discovered);
        var historyCountAfterFirstRun = await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId);
        await tool.ExecuteAsync(context);
        Assert.Equal(historyCountAfterFirstRun, await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId));
    }

    [Fact]
    public async Task ComputerUserProfileDiscoveryTool_CreatesProfileEntityDeterministically()
    {
        var provider = new FixedExecutionContextProvider(
            computerName: "test-computer",
            userName: "test-user",
            operatingSystemName: "windows",
            homeDirectoryPath: @"C:\Users\test-user");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(dataAccessLayer, provider);
        var tool = new ComputerUserProfileDiscoveryTool(provider);

        await tool.ExecuteAsync(context);

        var discovered = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName(
                "computer-user-profiles",
                "users",
                "username",
                "test-user",
                "computers",
                "hostname",
                "test-computer"));
        Assert.NotNull(discovered);
        var historyCountAfterFirstRun = await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId);
        await tool.ExecuteAsync(context);
        Assert.Equal(historyCountAfterFirstRun, await GetHistoryEntryCountAsync(dataAccessLayer, discovered.EntityId));
        Assert.Contains("\"home-directory\":\"C:\\\\Users\\\\test-user\"", discovered.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComputerUserProfileDiscoveryTool_WithProfileOverride_DivergesProfileNameButKeepsRealComputerReference()
    {
        var provider = new FixedExecutionContextProvider(
            computerName: "real-machine",
            userName: "test-user",
            operatingSystemName: "windows",
            homeDirectoryPath: @"C:\Users\test-user",
            effectiveComputerName: "override-machine");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(dataAccessLayer, provider);
        var tool = new ComputerUserProfileDiscoveryTool(provider);

        await tool.ExecuteAsync(context);

        var overrideProfile = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName(
                "computer-user-profiles",
                "users",
                "username",
                "test-user",
                "computers",
                "hostname",
                "override-machine"));
        Assert.NotNull(overrideProfile);

        var rawText = overrideProfile.Data?.GetRawText();
        Assert.Contains("[\"computers\",\"hostname\",\"real-machine\"]", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"computer-reference\":[\"computers\",\"hostname\",\"override-machine\"]", rawText, StringComparison.Ordinal);

        var realProfile = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName(
                "computer-user-profiles",
                "users",
                "username",
                "test-user",
                "computers",
                "hostname",
                "real-machine"));
        Assert.NotEqual(overrideProfile.EntityId, realProfile?.EntityId);
    }

    private static async Task<WorkspaceToolExecutionContext> CreateExecutionContextAsync(
        IDataAccessLayer dataAccessLayer,
        FixedExecutionContextProvider provider)
    {
        var currentComputerEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            $$"""
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["computer"],
              "names": [["computers", "hostname", "{{provider.ComputerName}}"]]
            }
            """,
            concurrencyTag: null);
        var currentUserEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            $$"""
            {
              "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["user"],
              "names": [["users", "username", "{{provider.UserName}}"]]
            }
            """,
            concurrencyTag: null);
        var currentComputerUserProfileEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            $$"""
            {
              "entity-id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "entity-types": ["user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "{{provider.UserName}}", "computers", "hostname", "{{provider.ComputerName}}"]],
              "computer-reference": ["computers", "hostname", "{{provider.ComputerName}}"],
              "user-reference": ["users", "username", "{{provider.UserName}}"],
              "home-directory": "{{provider.HomeDirectoryPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
            }
            """,
            concurrencyTag: null);

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = currentComputerEntity,
            CurrentUserEntity = currentUserEntity,
            CurrentComputerUserProfileEntity = currentComputerUserProfileEntity,
            ToolRelationship = currentComputerUserProfileEntity,
            Participants = [currentComputerUserProfileEntity],
            Tool = currentUserEntity,
            Schedule = currentComputerEntity,
        };
    }

    private static async Task<EntitySnapshot?> GetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            });
        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault();
    }

    private static async Task<int> GetHistoryEntryCountAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId)
    {
        var historyResult = await dataAccessLayer.GetHistoryAsync(
            new GetHistoryRequest
            {
                EntityIds = [entityId],
            });
        return Assert.Single(historyResult.History).UpdateTimes.Count;
    }

    private static async Task<EntitySnapshot> UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Discovery tools test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }

    private sealed class FixedExecutionContextProvider(
        string computerName,
        string userName,
        string operatingSystemName,
        string homeDirectoryPath,
        string? effectiveComputerName = null) : ICurrentExecutionContextProvider
    {
        public string ComputerName => computerName;

        public string UserName => userName;

        public string OperatingSystemName => operatingSystemName;

        public string HomeDirectoryPath => homeDirectoryPath;

        public string EffectiveComputerName => effectiveComputerName ?? computerName;
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentSessionDisplaySubscriptionTests
{
    [Fact]
    public async Task PersistedEntityAddedThenRemoved_AppliesLateMetadataAndUnsubscribesRemovedEntity()
    {
        await using var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());
        repository.SetDataAccessLayerForTesting(new InMemoryDataAccessLayer());
        var broker = new EntityBroker(repository);
        var sessionId = new AgentSessionId("late-session");
        var record = CreateRecord(sessionId, "");
        var records = new ObservableCollection<RunningAgentChatWithEntityInfo> { record };
        var table = new Mock<IRunningAgentChatTable>();
        table.SetupGet(value => value.RunningSessions).Returns(records);
        table.Setup(value => value.ApplyAuthoritativeDisplayName(sessionId, It.IsAny<string?>()))
            .Callback<AgentSessionId, string?>((_, name) => record.ApplyAuthoritativeDisplayName(name));
        using var subscription = await RunningAgentSessionDisplaySubscription.CreateAsync(
            broker,
            table.Object,
            TestContext.Current.CancellationToken);
        using var row = new RunningAgentRowViewModel(
            record,
            workspacePaneTitle: null,
            tabTitle: null,
            entityName: null,
            hasOpenTab: false,
            isThinking: false,
            activateCommand: new RelayCommand(_ => { }),
            interruptAsync: _ => Task.CompletedTask,
            terminateAsync: _ => Task.CompletedTask,
            setContinueInBackgroundAsync: (_, _) => Task.CompletedTask);
        Assert.False(row.HasOpenTab);
        Assert.Equal("Agent session late-ses", row.SessionTitle);

        var entityId = new EntityId(Guid.NewGuid());
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["entity-types"] = new[] { "agent-session" },
            ["agent-session-id"] = sessionId.Value,
            ["display-name"] = new Dictionary<string, string> { ["default"] = "Late persisted title" },
        });
        await broker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Create late session metadata" } },
            Changes = [new EntityChange { EntityId = entityId, Data = data, EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);
        Assert.Equal("Late persisted title", row.SessionTitle);
        var entity = Assert.Single(await broker.GetEntitiesAsync([entityId], TestContext.Current.CancellationToken));
        await entity.SaveDisplayNameAsync("Late rename");
        Assert.Equal("Late rename", row.SessionTitle);

        await broker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Remove session metadata" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = entity.Snapshot.ConcurrencyTag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, TestContext.Current.CancellationToken);
        var titleAfterRemoval = row.SessionTitle;
        table.Invocations.Clear();

        // A retained view model can still change after query removal; it must no longer publish.
        entity.UpdateSnapshot(new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = entity.Snapshot.ConcurrencyTag,
            ModifiedTime = entity.Snapshot.ModifiedTime,
            Data = data,
            Relationships = [],
        });
        table.Verify(
            value => value.ApplyAuthoritativeDisplayName(It.IsAny<AgentSessionId>(), It.IsAny<string?>()),
            Times.Never);
        Assert.Equal(titleAfterRemoval, row.SessionTitle);
        records.Remove(record);
        records.Add(record);
        table.Verify(
            value => value.ApplyAuthoritativeDisplayName(It.IsAny<AgentSessionId>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task PersistedRename_WithoutViewers_UpdatesCurrentRecordAndDefaultHeaders()
    {
        await using var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());
        var dataAccess = new InMemoryDataAccessLayer();
        repository.SetDataAccessLayerForTesting(dataAccess);
        var broker = new EntityBroker(repository);
        var entityId = new EntityId(Guid.NewGuid());
        var sessionId = new AgentSessionId("display-session");
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["entity-types"] = new[] { "agent-session" },
            ["agent-session-id"] = sessionId.Value,
            ["display-name"] = new Dictionary<string, string> { ["default"] = "Original" },
        });
        await broker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Create session" } },
            Changes = [new EntityChange { EntityId = entityId, Data = data, EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);
        var record = CreateRecord(sessionId, "");
        var records = new ObservableCollection<RunningAgentChatWithEntityInfo> { record };
        var table = new Mock<IRunningAgentChatTable>();
        table.SetupGet(value => value.RunningSessions).Returns(records);
        table.Setup(value => value.ApplyAuthoritativeDisplayName(sessionId, It.IsAny<string?>()))
            .Callback<AgentSessionId, string?>((_, name) =>
                records.SingleOrDefault()?.ApplyAuthoritativeDisplayName(name));
        using var subscription = await RunningAgentSessionDisplaySubscription.CreateAsync(
            broker, table.Object, TestContext.Current.CancellationToken);
        Assert.Equal("Original", record.SessionTitle);
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "default", Title = "stale" };
        var alias = new AgentSessionWorkspaceTabViewModel { Id = "alias", Title = "stale" };
        tab.ObserveSessionDisplay(record);
        alias.ObserveSessionDisplay(record);
        alias.SetTitleExplicit("Viewer alias");
        var entity = Assert.Single(await broker.GetEntitiesAsync([entityId], TestContext.Current.CancellationToken));
        await entity.SaveDisplayNameAsync("Renamed");
        Assert.Equal("Renamed", record.SessionTitle);
        Assert.Equal("Renamed", tab.Title);
        Assert.Equal("Viewer alias", alias.Title);
        Assert.False(tab.IsTitleExplicit);
        await tab.DisposeAsync();
        await alias.DisposeAsync();

        var replacement = CreateRecord(sessionId, "");
        records.Remove(record);
        records.Add(replacement);
        Assert.Equal("Renamed", replacement.SessionTitle);
        subscription.Dispose();
        await entity.SaveDisplayNameAsync("After disposal");
        Assert.Equal("Renamed", replacement.SessionTitle);
    }

    [Theory]
    [InlineData("short", " ", null, "Agent session short")]
    [InlineData("abcdefghijk", "", " \t", "Agent session abcdefgh")]
    [InlineData("short", "Launch label", " ", "Launch label")]
    [InlineData("", "", null, "Agent session (unknown)")]
    [InlineData("short", "Launch label", "Canonical", "Canonical")]
    [InlineData("abcdefghijk", "abcdefghijk", null, "Agent session abcdefgh")]
    [InlineData("abcdefghijk", "Launch label", "abcdefghijk", "abcdefghijk")]
    [InlineData("abcdefghijk", "ABCDEFGHIJK", null, "ABCDEFGHIJK")]
    [InlineData("short", "  Launch label \t", null, "  Launch label \t")]
    [InlineData("short", "Launch label", "  Canonical \t", "  Canonical \t")]
    public void TitlePolicy_ResolvesNonblankIdentityAndSuppressesUnchangedNotifications(
        string identifier, string launchName, string? canonicalName, string expected)
    {
        var record = CreateRecord(new AgentSessionId(identifier), launchName);
        record.ApplyAuthoritativeDisplayName(canonicalName);
        Assert.Equal(expected, record.SessionTitle);
        var notifications = 0;
        record.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(record.SessionTitle))
                notifications++;
        };
        record.ApplyAuthoritativeDisplayName(canonicalName);
        Assert.Equal(0, notifications);
        record.ApplyAuthoritativeDisplayName("Changed");
        Assert.Equal(1, notifications);
        Assert.Equal("Changed", record.SessionTitle);
        record.ApplyAuthoritativeDisplayName("Changed");
        Assert.Equal(1, notifications);
        record.ApplyAuthoritativeDisplayName(canonicalName);
        Assert.Equal(2, notifications);
        Assert.Equal(expected, record.SessionTitle);
    }

    [Fact]
    public void TitlePolicy_ClearingCanonicalRestoresLaunchOrIdentifier_WithChangeOnlyNotifications()
    {
        var record = CreateRecord(new AgentSessionId("abcdefghijk"), "Launch");
        var titles = new List<string>();
        record.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(record.SessionTitle))
                titles.Add(record.SessionTitle);
        };

        record.ApplyAuthoritativeDisplayName("Canonical");
        record.ApplyLaunchName("New launch");
        record.ApplyAuthoritativeDisplayName(" ");
        record.ApplyAuthoritativeDisplayName(null);
        record.ApplyLaunchName("New launch");
        record.ApplyLaunchName("abcdefghijk");
        record.ApplyLaunchName("");
        record.ApplyLaunchName(" \t");

        Assert.Equal(new[] { "Canonical", "New launch", "Agent session abcdefgh" }, titles);
        Assert.Equal("Agent session abcdefgh", record.SessionTitle);
    }

    private static RunningAgentChatWithEntityInfo CreateRecord(AgentSessionId sessionId, string name)
        => new(sessionId, false, _ => throw new InvalidOperationException(), name, null);

    [Theory]
    [InlineData("short", "Entity", "Entity")]
    [InlineData("short", "  Entity \t", "  Entity \t")]
    [InlineData("abcdefghijk", "abcdefghijk", "abcdefghijk")]
    [InlineData("short", " ", "Agent session short")]
    [InlineData("abcdefghijk", "", "Agent session abcdefgh")]
    [InlineData("", "", "Agent session (unknown)")]
    public void StandaloneEntityRowTitlePolicy_CoversEveryFallback(
        string identifier,
        string name,
        string expected)
    {
        using var row = new RunningAgentRowViewModel(
            identifier,
            name,
            new RelayCommand(_ => { }));
        Assert.Equal(expected, row.SessionTitle);
    }

    [Theory]
    [InlineData("short", "Tab", "Pane", "Tab")]
    [InlineData("short", "  Tab \t", "Pane", "  Tab \t")]
    [InlineData("short", " ", "Pane", "Pane")]
    [InlineData("short", "", "  Pane \t", "  Pane \t")]
    [InlineData("short", "", " ", "Agent session short")]
    [InlineData("abcdefghijk", "", "", "Agent session abcdefgh")]
    [InlineData("", "", "", "Agent session (unknown)")]
    public void StandaloneViewerRowTitlePolicy_CoversEveryFallback(
        string identifier,
        string tabTitle,
        string paneTitle,
        string expected)
    {
        using var row = new RunningAgentRowViewModel(
            identifier,
            paneTitle,
            tabTitle,
            false,
            new RelayCommand(_ => { }));
        Assert.Equal(expected, row.SessionTitle);
    }

    [Theory]
    [InlineData("Acquisition", null, "Acquisition")]
    [InlineData("Acquisition", "Canonical", "Canonical")]
    [InlineData("", " ", "Agent session abcdefgh")]
    public void RuntimeRowTitlePolicy_IgnoresViewerSeeds_ObservesRenamesUntilDisposed(
        string acquisitionName,
        string? canonicalName,
        string expected)
    {
        var record = CreateRecord(new AgentSessionId("abcdefghijk"), acquisitionName);
        record.ApplyAuthoritativeDisplayName(canonicalName);
        using var row = new RunningAgentRowViewModel(
            record,
            workspacePaneTitle: "Pane seed",
            tabTitle: "Tab seed",
            entityName: "Entity seed",
            hasOpenTab: true,
            isThinking: false,
            activateCommand: new RelayCommand(_ => { }),
            interruptAsync: _ => Task.CompletedTask,
            terminateAsync: _ => Task.CompletedTask,
            setContinueInBackgroundAsync: (_, _) => Task.CompletedTask);
        Assert.Equal(expected, row.SessionTitle);

        record.ApplyAuthoritativeDisplayName("Renamed");
        Assert.Equal("Renamed", row.SessionTitle);
        row.Dispose();
        record.ApplyAuthoritativeDisplayName("After disposal");
        Assert.Equal("Renamed", row.SessionTitle);
    }
}

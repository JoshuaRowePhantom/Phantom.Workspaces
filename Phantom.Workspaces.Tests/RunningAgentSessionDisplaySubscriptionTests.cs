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
    }

    private static RunningAgentChatWithEntityInfo CreateRecord(AgentSessionId sessionId, string name)
        => new(sessionId, false, _ => throw new InvalidOperationException(), name, null);

    [Theory]
    [InlineData("short", "Entity", "Tab", "Pane", "Entity")]
    [InlineData("short", " ", "Tab", "Pane", "Tab")]
    [InlineData("short", null, " ", "Pane", "Pane")]
    [InlineData("short", null, "", " ", "Agent session short")]
    [InlineData("abcdefghijk", "", "", "", "Agent session abcdefgh")]
    [InlineData("", null, "", "", "Agent session (unknown)")]
    public void StandaloneRowTitlePolicy_CoversEveryFallback(
        string identifier, string? name, string tabTitle, string paneTitle, string expected)
    {
        using var row = !string.IsNullOrWhiteSpace(name)
            ? new RunningAgentRowViewModel(identifier, name, new RelayCommand(_ => { }))
            : new RunningAgentRowViewModel(identifier, paneTitle, tabTitle, false, new RelayCommand(_ => { }));
        Assert.Equal(expected, row.SessionTitle);
    }
}

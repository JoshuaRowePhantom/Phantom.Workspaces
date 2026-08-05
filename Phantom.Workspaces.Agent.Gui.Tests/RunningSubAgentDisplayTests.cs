using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class RunningSubAgentDisplayTests
{
    private static AgentChatRunningItemCollection CreateRunningItems()
        => new AgentChatRunningItemCollection();

    private static AgentChatRunningItem CreateRunningItem(AgentChatRunningItemCollection collection)
    {
        var item = new AgentChatRunningItem();
        collection.Add(item);
        return item;
    }

    private static AgentChatHistoryItem TextHistoryItem(string text)
        => new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static AgentChatHistoryItem ToolCallHistoryItem(string toolName)
        => new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-id", toolName)],
        };

    [Fact]
    public void WhenRunningItemAdded_SubscribesToItsItems()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("hello"));

        Assert.Single(sut.RecentActivity);
    }

    [Fact]
    public void WhenRunningItemRemoved_UnsubscribesFromItsItems()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        runningItems.Remove(item);

        item.Items.Add(TextHistoryItem("should be ignored"));

        Assert.Empty(sut.RecentActivity);
    }

    [Fact]
    public void WhenItemWithTextArrives_AddsAgentTextActivityLine()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("some agent output"));

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal(SubAgentActivityKind.AgentText, line.Kind);
        Assert.Equal("some agent output", line.Text);
    }

    [Fact]
    public void WhenItemWithToolCallArrives_AddsToolCallActivityLine()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(ToolCallHistoryItem("read_file"));

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal(SubAgentActivityKind.ToolCall, line.Kind);
        Assert.Equal("read_file", line.Text);
    }

    [Fact]
    public void RecentActivity_IsCappedAtMaxActivityLines()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);

        for (var i = 1; i <= 7; i++)
            item.Items.Add(TextHistoryItem($"line {i}"));

        Assert.Equal(RunningSubAgentDisplay.MaxActivityLines, sut.RecentActivity.Count);
        Assert.Equal("line 3", sut.RecentActivity[0].Text);
        Assert.Equal("line 7", sut.RecentActivity[4].Text);
    }

    [Fact]
    public void RecentActivity_OnReplace_UpdatesExistingEntry_NotAppends()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("first"));
        item.Items[0] = TextHistoryItem("first updated");

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal("first updated", line.Text);
    }

    [Fact]
    public void RecentActivity_StreamingTokens_ShowsOnlyOneEntryPerMessage()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("a"));
        item.Items[0] = TextHistoryItem("ab");
        item.Items[0] = TextHistoryItem("abc");
        item.Items[0] = TextHistoryItem("abcd");

        var line = Assert.Single(sut.RecentActivity);
        Assert.Equal("abcd", line.Text);
    }

    [Fact]
    public void SubscribeToRunningItem_AfterSetItem_SingleHandlerActive()
    {
        var runningItems = CreateRunningItems();
        var item = CreateRunningItem(runningItems);
        using var sut = new RunningSubAgentDisplay(runningItems);

        var fired = 0;
        sut.ActivityChanged += (_, _) => fired++;

        runningItems.SetItem(0, item);
        item.Items.Add(TextHistoryItem("hello"));

        Assert.Equal(1, fired);
        Assert.Single(sut.RecentActivity);
    }

    [Fact]
    public void ActivityChanged_FiresWhenActivityLineAdded()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var fired = 0;
        sut.ActivityChanged += (_, _) => fired++;

        var item = CreateRunningItem(runningItems);
        item.Items.Add(TextHistoryItem("hello"));
        item.Items.Add(ToolCallHistoryItem("write_file"));

        Assert.Equal(2, fired);
    }

    [Fact]
    public void ActivityChanged_DoesNotFire_WhenItemHasNoRecognisedContent()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var fired = 0;
        sut.ActivityChanged += (_, _) => fired++;

        var item = CreateRunningItem(runningItems);
        item.Items.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [] });

        Assert.Equal(0, fired);
    }

    [Fact]
    public void WhenRunningItemItemsCollectionChanges_RecentActivityUpdated()
    {
        var runningItems = CreateRunningItems();
        using var sut = new RunningSubAgentDisplay(runningItems);

        var item = CreateRunningItem(runningItems);

        item.Items.Add(TextHistoryItem("first"));
        Assert.Single(sut.RecentActivity);
        Assert.Equal("first", sut.RecentActivity[0].Text);

        item.Items.Add(ToolCallHistoryItem("my_tool"));
        Assert.Equal(2, sut.RecentActivity.Count);
        Assert.Equal(SubAgentActivityKind.ToolCall, sut.RecentActivity[1].Kind);
    }

    [Fact]
    public async Task RunningSubAgentDisplay_Description_ReflectsAgentChatDescription()
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "description": "This is a test description",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = definition,
            });

        await using var _ = chat;
        var display = new RunningSubAgentDisplay(chat);

        Assert.Equal("This is a test description", display.Description);
    }

    [Fact]
    public async Task RunningSubAgentDisplay_FromAgentChat_ExposesProvidedDisplayName()
    {
        // #1132: RunningSubAgentDisplay must surface the sub-agent's provided display name
        // (from AgentChat.DisplayName) so the [Running sub-agents] panel data source
        // carries the correct per-sub-agent label rather than a hard-coded generic label.
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "fix-reload1",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = definition,
            });

        await using var _ = chat;
        var display = new RunningSubAgentDisplay(chat);

        Assert.Equal(chat.DisplayName, display.DisplayName);
        Assert.DoesNotContain("GitHub Copilot Sub-Agent", display.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubAgentDisplayName_WhenProvidedNameFlowedThroughFactory_SurfacesProvidedName()
    {
        // Fix #1133 (view side, over the fixed model): when the sub-agent AgentChat is
        // constructed with a caller-provided display name (as the Copilot SDK router now does
        // by mapping the "display-name" lifecycle argument to DisplayNameOverride), the
        // RunningSubAgentDisplay data source must surface the provided name — never the
        // freshly-generated session GUID that used to appear on the card header, and never
        // the fallback definition name.
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "generic-sub-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        const string providedName = "fix-reload1";
        var chat = await Phantom.Workspaces.Llm.AgentChat.CreateAsync(
            new Phantom.Workspaces.Llm.InternalCreateAgentChatRequest
            {
                AgentDefinition = definition,
                ConfiguredStore = new Phantom.Workspaces.Llm.InMemoryAgentPersistenceStore(),
                ClientOverride = new Phantom.Workspaces.Llm.DeterministicTestChatClient(),
                DisplayNameOverride = providedName,
            });

        await using var _ = chat;
        var display = new RunningSubAgentDisplay(chat);

        Assert.Equal(providedName, display.DisplayName);
        // The bug's fingerprint was a 32-hex GUID from Guid.NewGuid().ToString("n").
        Assert.DoesNotMatch("^[0-9a-f]{32}$", display.DisplayName);
        // And it must not fall back to the definition name either.
        Assert.NotEqual("generic-sub-agent", display.DisplayName);
    }

    [Fact]
    public async Task SubAgentName_WhenCallerNameSet_SurfacesCallerNameNotGuid()
    {
        // Fix #1151: when the sub-agent AgentChat carries a caller-supplied name (e.g.
        // "fix-crash1142" from SubagentStartedData.AgentName, threaded through
        // InternalCreateAgentChatRequest.NameOverride), the RunningSubAgentDisplay must expose
        // that name via IRunningSubAgentDisplay.Name — distinct from the type-level DisplayName —
        // instead of leaving it empty and forcing the operator to correlate GUIDs.
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "generic-sub-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        const string callerName = "fix-crash1142";
        var chat = await Phantom.Workspaces.Llm.AgentChat.CreateAsync(
            new Phantom.Workspaces.Llm.InternalCreateAgentChatRequest
            {
                AgentDefinition = definition,
                ConfiguredStore = new Phantom.Workspaces.Llm.InMemoryAgentPersistenceStore(),
                ClientOverride = new Phantom.Workspaces.Llm.DeterministicTestChatClient(),
                DisplayNameOverride = "General purpose",
                NameOverride = callerName,
            });

        await using var _ = chat;
        var display = new RunningSubAgentDisplay(chat);

        Assert.Equal(callerName, display.Name);
        // The bug's fingerprint was a 32-hex GUID surface. The caller-supplied name must not be it.
        Assert.DoesNotMatch("^[0-9a-f]{32}$", display.Name);
        // And it must remain independent of the type-level display name.
        Assert.Equal("General purpose", display.DisplayName);
        Assert.NotEqual(display.DisplayName, display.Name);
    }
}

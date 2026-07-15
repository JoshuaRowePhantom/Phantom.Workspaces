using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;
using Phantom.Workspaces.Utilities;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionWorkspaceTabIntegrationTests
{
    [Fact]
    public async Task RenameSession_UpdatesEntityDisplayName_AndTabTitle()
    {
        await using var fixture = await SlashCommandFixture.CreateAsync();

        await fixture.RunSlashCommandAsync("/rename Renamed Session");

        Assert.Equal("Renamed Session", fixture.EntityDisplayName);
        Assert.Equal("Renamed Session", fixture.CurrentTabTitle);
    }

    [Fact]
    public async Task SetTabTitle_DoesNotUpdateEntityDisplayName()
    {
        await using var fixture = await SlashCommandFixture.CreateAsync();

        await fixture.RunSlashCommandAsync("/title Temporary Title");

        Assert.Equal("Original Session", fixture.EntityDisplayName);
        Assert.Equal("Temporary Title", fixture.CurrentTabTitle);
    }

    [Fact]
    public async Task RestartSlashCommand_ReplacesTab_WithClonedSession()
    {
        await using var fixture = await SlashCommandFixture.CreateAsync();
        var originalEntityId = fixture.CurrentEntityId;

        await fixture.RunSlashCommandAsync("/restart");

        Assert.NotEqual(originalEntityId, fixture.CurrentEntityId);
        Assert.Equal("Original Session (2)", fixture.EntityDisplayName);
        Assert.Equal("Original Session (2)", fixture.CurrentTabTitle);
        Assert.Contains(originalEntityId, fixture.HistoricalEntityIds);
    }

    [Fact]
    public async Task CloneSlashCommand_OpensNewTab_WithClonedSession()
    {
        await using var fixture = await SlashCommandFixture.CreateAsync();
        var originalEntityId = fixture.CurrentEntityId;

        await fixture.RunSlashCommandAsync("/clone");

        Assert.Equal(originalEntityId, fixture.CurrentEntityId);
        Assert.Equal("Original Session", fixture.EntityDisplayName);
        Assert.Equal("Original Session", fixture.CurrentTabTitle);
        Assert.Single(fixture.OpenedCloneNames);
        Assert.Equal("Original Session (2)", fixture.OpenedCloneNames.Single());
    }

    private sealed class SlashCommandFixture : IAsyncDisposable
    {
        private readonly AgentChat chat;
        private readonly AgentViewModel viewModel;
        private readonly ObservableLoggerFactory loggerFactory;
        private readonly List<string> existingDisplayNames = ["Original Session"];
        private readonly JsonElement originalEntityData;

        private SlashCommandFixture(
            AgentChat chat,
            AgentViewModel viewModel,
            ObservableLoggerFactory loggerFactory,
            JsonElement originalEntityData)
        {
            this.chat = chat;
            this.viewModel = viewModel;
            this.loggerFactory = loggerFactory;
            this.originalEntityData = originalEntityData;
        }

        public string EntityDisplayName { get; private set; } = "Original Session";

        public string CurrentTabTitle { get; private set; } = "Original Session";

        public EntityId CurrentEntityId { get; private set; } = new("00000000-0000-0000-0000-000000000001");

        public List<EntityId> HistoricalEntityIds { get; } = [];

        public List<string> OpenedCloneNames { get; } = [];

        public static async Task<SlashCommandFixture> CreateAsync()
        {
            var chat = await AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
            var loggerFactory = new ObservableLoggerFactory();
            var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);
            using var document = JsonDocument.Parse("""
            {
              "entity-id": "00000000-0000-0000-0000-000000000001",
              "display-name": "Original Session"
            }
            """);
            var fixture = new SlashCommandFixture(chat, viewModel, loggerFactory, document.RootElement.Clone());
            viewModel.ConfigureSlashCommands(() => fixture.CreateContext());
            return fixture;
        }

        public async Task RunSlashCommandAsync(string command)
        {
            var interceptor = this.viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
            await interceptor(command);
        }

        public async ValueTask DisposeAsync()
        {
            await this.viewModel.DisposeAsync();
            await this.chat.DisposeAsync();
            this.loggerFactory.Dispose();
        }

        private SlashCommandContext CreateContext()
            => new()
            {
                AgentChat = this.chat,
                RenameSessionAsync = (newName, ct) =>
                {
                    this.EntityDisplayName = newName;
                    this.CurrentTabTitle = newName;
                    this.ReplaceCurrentName(newName);
                    return Task.CompletedTask;
                },
                SetTabTitleAsync = (newTitle, ct) =>
                {
                    this.CurrentTabTitle = newTitle;
                    return Task.CompletedTask;
                },
                ReplaceWithCloneAsync = ct =>
                {
                    var (cloneId, cloneName) = this.CreateClone();
                    this.HistoricalEntityIds.Add(this.CurrentEntityId);
                    this.CurrentEntityId = cloneId;
                    this.EntityDisplayName = cloneName;
                    this.CurrentTabTitle = cloneName;
                    this.ReplaceCurrentName(cloneName);
                    return Task.CompletedTask;
                },
                OpenCloneInNewTabAsync = ct =>
                {
                    var (_, cloneName) = this.CreateClone();
                    this.OpenedCloneNames.Add(cloneName);
                    this.existingDisplayNames.Add(cloneName);
                    return Task.CompletedTask;
                },
            };

        private (EntityId EntityId, string DisplayName) CreateClone()
        {
            var cloneId = new EntityId();
            var cloneData = EntityCloneHelper.RewriteEntityId(this.originalEntityData, cloneId);
            var cloneName = DisplayNameSuffixHelper.GetNextAvailableName(this.EntityDisplayName, this.existingDisplayNames);
            Assert.Equal(cloneId.ToString(), cloneData.GetProperty("entity-id").GetString());
            return (cloneId, cloneName);
        }

        private void ReplaceCurrentName(string name)
        {
            this.existingDisplayNames.Clear();
            this.existingDisplayNames.Add(name);
        }
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);
}

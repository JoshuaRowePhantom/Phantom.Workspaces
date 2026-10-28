using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Microsoft.Extensions.AI;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentStartupProgressTests
{
    [AvaloniaFact]
    public async Task Observe_EmptyConstructingChat_RetainsLifecycleUntilRealDiagnosticsAndForwardsLaterStages()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"startup","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        using var progress = new AgentStartupProgress();
        progress.Report("Research", "service-initialization");
        progress.Observe(chat);
        Assert.Contains("Preparing session", Assert.Single(Assert.Single(progress.RunningItems).Items).Contents.OfType<TextContent>().Single().Text);
        progress.Report("Research", "remote-flight-chat-acquisition");
        Assert.Contains("Loading session and history", Assert.Single(Assert.Single(progress.RunningItems).Items).Contents.OfType<TextContent>().Single().Text);
        var diagnostic = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [new TextContent("Loading tools from Example MCP")],
        });
        Assert.Same(diagnostic, Assert.Single(progress.RunningItems));
        progress.Report("Research", "ready-publication");
        Assert.Same(diagnostic, Assert.Single(progress.RunningItems));
        chat.CompleteRunningItem(diagnostic, false);
        Assert.Contains("Opening chat", Assert.Single(Assert.Single(progress.RunningItems).Items).Contents.OfType<TextContent>().Single().Text);
    }

    [AvaloniaFact]
    public async Task Observe_ConstructingChat_ForwardsLiveDiagnosticsWithoutOwningRuntime()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"startup","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        using var progress = new AgentStartupProgress();
        progress.Observe(chat);
        var item = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [new TextContent("Waiting for your authorization: GitHub")],
        });
        Assert.Contains(item, progress.RunningItems);
        progress.Report("session", "ready-publication");
        Assert.Contains(item, progress.RunningItems);
        chat.CompleteRunningItem(item, false);
        Assert.DoesNotContain(item, progress.RunningItems);
        var failed = new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [new TextContent("Example MCP tool loading failed")],
        };
        chat.CompleteRunningItem(chat.CreateRunningItem(failed));
        Assert.Contains(failed, progress.History);
        progress.Dispose();
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        chat.History.Add(new AgentChatHistoryItem { Role = ChatRole.User });
        Assert.Empty(progress.RunningItems);
        Assert.Empty(progress.History);
    }

    [AvaloniaFact]
    public async Task IsChatRunning_StartupDiagnostics_DoNotImpersonateTurns()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"startup","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        using var logger = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(chat, "startup", "", logger, TaskScheduler.Default);
        var startup = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [new TextContent("Waiting for your authorization: GitHub")],
        });
        Assert.False(view.IsChatRunning);
        chat.CompleteRunningItem(startup, false);
        Assert.False(view.IsChatRunning);
        var turn = chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("working")] });
        Assert.True(view.IsChatRunning);
        chat.CompleteRunningItem(turn, false);
        Assert.False(view.IsChatRunning);
    }

    [Theory]
    [InlineData("service-initialization", "Preparing session")]
    [InlineData("owner-decision", "Waiting for your owner selection")]
    [InlineData("transport-choice", "Connecting to owner")]
    [InlineData("remote-flight-chat-acquisition", "Loading session and history")]
    [InlineData("ready-publication", "Opening chat")]
    [InlineData("toolset", "Loading toolset")]
    public void Report_NamedStartupStep_UsesTransientEncodedDiagnostic(string stage, string expected)
    {
        var progress = new AgentStartupProgress();
        progress.Report("<session>", stage);
        var item = Assert.Single(Assert.Single(progress.RunningItems).Items);
        var html = ChatOutputHtmlRenderer.RenderContent("startup", item.Contents[0], false, true)!;
        Assert.Contains(expected, html);
        Assert.Contains("&lt;session&gt;", html);
        Assert.Empty(progress.History);
        progress.Report("next", "ready-publication");
        Assert.Single(progress.RunningItems);
    }
}

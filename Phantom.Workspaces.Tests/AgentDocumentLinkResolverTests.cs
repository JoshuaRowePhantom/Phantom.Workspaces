using Phantom.Workspaces.ViewModels;
using Xunit;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDocumentLinkResolverTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptLink_RecordedGenerationDirectory_OpensCorrectBrowserAndLegacyOrForeignOriginFailsSafely(bool foreignMachine)
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","link-origin"]],"agent-session-id":"origin-session","display-name":{"default":"Origin"}}""");
        await using var handler = new OpenAgentSessionShortcutHandler(new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"origin","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        using var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel { Id = entityId.ToString(), Title = "Origin", Entity = entity };
        var agent = handler.ComposeSessionAgentViewModel(new ComposeSessionAgentViewModelOptions
        {
            MainWindowViewModel = main, LoggerFactory = logger, AgentChat = chat,
            AgentSessionEntity = entity, Tab = tab, ForegroundScheduler = TaskScheduler.Default,
        });
        tab.SetReady(agent, logger);
        await main.OpenTabAsync(tab);
        chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant, AssistantRunId = "historical-run",
            DocumentOrigin = new AgentDocumentOrigin(@"C:\original\directory", true, MachineName: Environment.MachineName),
            Contents = [new TextContent("[report](report.md)")],
        });
        await agent.OpenDocumentLinkAsync("report.md", "historical-run", fromTranscript: true);
        var browser = Assert.Single(main.WorkspacePanes.SelectMany(p => p.Tabs).OfType<WebViewModel>());
        Assert.Equal("file:///C:/original/directory/report.md", browser.AddressBarUrl);
        Assert.Equal(browser.AddressBarUrl, browser.SourceUri!.AbsoluteUri);
        Assert.Equal(browser.AddressBarUrl, browser.HomeUrl);
        Assert.Equal(main.FindWorkspacePaneIdForTab(tab.Id), main.FindWorkspacePaneIdForTab(browser.Id));
        var errorVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (args.NewItems?.OfType<AgentChatHistoryItem>().Any(item => item.Contents.OfType<TextContent>().Any(content =>
                content.Text?.Contains("Unable to open document", StringComparison.Ordinal) == true)) == true)
                errorVisible.TrySetResult();
        }
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged += OnHistoryChanged;
        await agent.OpenDocumentLinkAsync("legacy.md", "unknown-old-run", fromTranscript: true);
        await errorVisible.Task;
        errorVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant, AssistantRunId = "foreign-run",
            DocumentOrigin = new AgentDocumentOrigin(@"C:\foreign\directory", true,
                foreignMachine ? null : "different-owner-profile",
                foreignMachine ? "different-machine" : Environment.MachineName),
            Contents = [new TextContent("[report](report.md)")],
        });
        await agent.OpenDocumentLinkAsync("report.md", "foreign-run", fromTranscript: true);
        await errorVisible.Task;
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged -= OnHistoryChanged;
        Assert.Single(main.WorkspacePanes.SelectMany(p => p.Tabs).OfType<WebViewModel>());
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(content =>
            content.Text?.Contains("Unable to open document", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("report.md", "file:///C:/work/agent/report.md")]
    [InlineData("../report.md", "file:///C:/work/report.md")]
    [InlineData("./a%20b%23%25.md#section", "file:///C:/work/agent/a%20b%23%25.md#section")]
    [InlineData("C:\\other\\report.md", "file:///C:/other/report.md")]
    [InlineData("\\\\server\\share\\report.md", "file://server/share/report.md")]
    [InlineData("https://example.com/report.md", "https://example.com/report.md")]
    [InlineData("mailto:person@example.com", "mailto:person@example.com")]
    public void Resolve_SupportedReference_ProducesAbsoluteBrowserSource(string reference, string expected)
    {
        var destination = AgentDocumentLinkResolver.Resolve(reference, @"C:\work\agent", true);
        Assert.Equal(expected, destination.AbsoluteUri);
        var browser = new WebViewModel(destination.AbsoluteUri, null!) { Id = "document", Title = "Document" };
        Assert.Equal(destination, browser.SourceUri);
        Assert.Equal(destination.AbsoluteUri, browser.AddressBarUrl);
        Assert.Equal(destination.AbsoluteUri, browser.HomeUrl);
    }

    [Theory]
    [InlineData("report.md", null, true)]
    [InlineData("report.md", "relative", true)]
    [InlineData("report.md", "C:\\work", false)]
    [InlineData("file:///C:/work/report.md", "C:\\work", false)]
    [InlineData("javascript:alert(1)", "C:\\work", true)]
    public void Resolve_UnsupportedOrUnownedReference_FailsVisibly(
        string reference,
        string? directory,
        bool isLocal)
        => Assert.Throws<InvalidOperationException>(() => AgentDocumentLinkResolver.Resolve(reference, directory, isLocal));
}

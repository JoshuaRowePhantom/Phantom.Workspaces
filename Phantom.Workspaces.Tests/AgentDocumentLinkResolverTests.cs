using Phantom.Workspaces.ViewModels;
using Xunit;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDocumentLinkResolverTests
{
    [AvaloniaFact]
    public async Task TranscriptLink_RuntimeDirectoryChanges_PersistReloadAndChildUseGenerationOrigin()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var store = new InMemoryAgentPersistenceStore();
        var responses = new DeterministicTestChatClient();
        var stream = responses.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "[report](report.md)"));
        stream.Complete();
        using var sdk = new CopilotSdkChatClient("test", "test", null, null);
        sdk.SetWorkingDirectory(@"C:\generation\original");
        using var client = new DocumentRuntimeClient(responses, sdk);
        await using var factory = new AgentChatFactory(store,
            new AgentServices { ChatClientOverride = client }, TaskScheduler.FromCurrentSynchronizationContext());
        var definition = AgentDefinitionLoader.LoadAgentFromJson("""
            {"kind":"prompt","name":"origin","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
            """);
        var sessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId(Guid.NewGuid().ToString());
        var lease = await factory.GetOrCreateAsync(sessionId, definition);
        var chat = lease.LocalAgentChat;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.TurnCompleted += (_, _) => completed.TrySetResult();
        chat.EnqueueUserMessage("report");
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var original = Assert.Single(chat.History, item => item.Role == ChatRole.Assistant);
        Assert.Equal(@"C:\generation\original", original.DocumentOrigin?.WorkingDirectory);
        Assert.True(original.DocumentOrigin?.IsLocalFileSystem);
        sdk.SetWorkingDirectory(@"C:\runtime\changed");
        await lease.DisposeAsync();
        await using var reloaded = await factory.GetAsync(sessionId);
        var restored = Assert.Single(reloaded.AgentChat.History, item => item.Role == ChatRole.Assistant);
        Assert.Equal(original.DocumentOrigin, restored.DocumentOrigin);
        Assert.Equal(@"C:\runtime\changed", reloaded.LocalAgentChat.DocumentWorkingDirectory);

        var childSink = await reloaded.LocalAgentChat.GetOrCreateAsync("sdk-child", definition, "child-call");
        var child = Assert.IsType<AgentChat>(Assert.Single(reloaded.LocalAgentChat.SubAgents));
        sdk.SetWorkingDirectory(@"C:\parent\later");
        var childCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)child.History).CollectionChanged += (_, _) =>
        {
            if (child.History.Any(item => item.Role == ChatRole.Assistant)) childCompleted.TrySetResult();
        };
        childSink.Push(new ChatResponseUpdate(ChatRole.Assistant, "[child report](child.md)"));
        childSink.Complete();
        await childCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var childMessage = Assert.Single(child.History, item => item.Role == ChatRole.Assistant);
        Assert.Equal(@"C:\runtime\changed", child.DocumentWorkingDirectory);
        Assert.True(child.IsLocalDocumentFileSystem);
        Assert.Equal(@"C:\runtime\changed", childMessage.DocumentOrigin?.WorkingDirectory);
        Assert.True(childMessage.DocumentOrigin?.IsLocalFileSystem);

        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","generation-link"]],"agent-session-id":"{{{sessionId.Value}}}"}""");
        await using var handler = new OpenAgentSessionShortcutHandler(new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());
        using var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel { Id = entityId.ToString(), Title = "Origin", Entity = entity };
        var agent = handler.ComposeSessionAgentViewModel(new ComposeSessionAgentViewModelOptions
        {
            MainWindowViewModel = main, LoggerFactory = logger, AgentChat = reloaded.AgentChat,
            AgentSessionEntity = entity, Tab = tab, ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
        });
        tab.SetLease(reloaded);
        tab.SetReady(agent, logger);
        await main.OpenTabAsync(tab);
        await agent.OpenDocumentLinkAsync("report.md", restored.AssistantRunId, fromTranscript: true);
        Assert.Equal("file:///C:/generation/original/report.md",
            Assert.Single(main.WorkspacePanes.SelectMany(pane => pane.Tabs).OfType<WebViewModel>()).SourceUri!.AbsoluteUri);
        var childView = Assert.Single(agent.SubAgentsContainer.Slots).SubAgentViewModel;
        Assert.Same(child, childView.AgentChat);
        await childView.OpenDocumentLinkAsync("child.md", childMessage.AssistantRunId, fromTranscript: true);
        Assert.Contains(main.WorkspacePanes.SelectMany(pane => pane.Tabs).OfType<WebViewModel>(),
            browser => browser.SourceUri?.AbsoluteUri == "file:///C:/runtime/changed/child.md");
    }

    private sealed class DocumentRuntimeClient(
        IChatClient inner,
        CopilotSdkChatClient runtime) : DelegatingChatClient(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(CopilotSdkChatClient) ? runtime : base.GetService(serviceType, serviceKey);
    }

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
    [InlineData("./caf\u00e9%20\u65e5\u672c%23%25.md#section", "file:///C:/work/agent/caf%C3%A9%20%E6%97%A5%E6%9C%AC%23%25.md#section")]
    [InlineData("./caf%C3%A9%20%E6%97%A5%E6%9C%AC%23%25.md#section", "file:///C:/work/agent/caf%C3%A9%20%E6%97%A5%E6%9C%AC%23%25.md#section")]
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

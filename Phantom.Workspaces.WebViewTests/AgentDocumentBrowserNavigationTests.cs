using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.WebViewTests;

[Collection(MainWindowWebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class AgentDocumentBrowserNavigationTests(MainWindowWebViewFixture fixture)
{
    [Fact]
    public Task ShellClick_LocalDocument_NavigatesActualDestinationBrowserAndAddress()
        => fixture.InvokeAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"phantom-document-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "caf\u00e9 \u65e5\u672c#%.html");
            await File.WriteAllTextAsync(path, "<html><title>Controlled report</title><body id=\"section\">Report destination</body></html>");
            await using var main = new MainWindowViewModel(new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await main.InitializeAsync();
            await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                    {"kind":"prompt","name":"browser-origin","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                    """),
            });
            chat.History.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant, AssistantRunId = "document-run",
                DocumentOrigin = new AgentDocumentOrigin(directory, true, MachineName: Environment.MachineName),
                Contents = [new TextContent("[report](caf%C3%A9%20%E6%97%A5%E6%9C%AC%23%25.html#section)")],
            });
            var entityId = new EntityId(Guid.NewGuid());
            var entity = new SubscribedEntityViewModel(new EntitySnapshot
            {
                EntityId = entityId,
                ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
                Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["entity-id"] = entityId.ToString(), ["entity-types"] = new[] { "entity", "agent-session" },
                    ["agent-session-id"] = chat.AgentSessionId, ["display-name"] = "Browser origin",
                }),
                Relationships = [],
            });
            await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), new AgentServices(), TaskScheduler.Default);
            await using var handler = new OpenAgentSessionShortcutHandler(new AgentSessionShortcutContext(),
                new UnusedExecutorSelector(), new RunningAgentChatTable(factory));
            using var logger = new ObservableLoggerFactory();
            var tab = new AgentSessionWorkspaceTabViewModel { Id = entityId.ToString(), Title = "Browser origin", Entity = entity };
            var agent = handler.ComposeSessionAgentViewModel(new ComposeSessionAgentViewModelOptions
            {
                MainWindowViewModel = main, LoggerFactory = logger, AgentChat = chat, AgentSessionEntity = entity,
                Tab = tab, ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            });
            tab.SetReady(agent, logger);
            await main.OpenTabAsync(tab);
            var output = new AgentChatOutputControl { DataContext = agent };
            var shell = (ControllableWebViewControl)typeof(AgentChatOutputControl)
                .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(output)!;
            var destinationHost = new ContentControl();
            var panel = new StackPanel { Children = { output, destinationHost } };
            var window = new Window
            {
                Width = 700, Height = 600, ShowInTaskbar = false, Content = panel,
                Position = new PixelPoint(-4000, -4000), WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Ready += (_, _) => ready.TrySetResult();
            ConfiguredWebView? destinationBrowser = null;
            WebViewModel? destinationModel = null;
            var pane = main.SelectedWorkspacePane;
            void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
            {
                if (args.NewItems?.OfType<WebViewModel>().SingleOrDefault() is not { } browserModel) return;
                destinationModel = browserModel;
                var view = new WebBrowserTabView();
                destinationBrowser = Assert.IsType<ConfiguredWebView>(view.FindControl<Border>("WebViewHost")!.Child);
                destinationBrowser.NavigationCompleted += (_, _) => navigated.TrySetResult();
                view.DataContext = browserModel;
                destinationHost.Content = view;
            }
            ((INotifyCollectionChanged)pane.Tabs).CollectionChanged += OnTabsChanged;
            try
            {
                window.Show();
                await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
                await output.HistoryLoaded.WaitAsync(TestContext.Current.CancellationToken);
                shell.EndBatch();
                await shell.InvokeScript("document.querySelector('a[href^=\"caf\"]').click();true");
                await navigated.Task.WaitAsync(TestContext.Current.CancellationToken);
                var expected = new Uri(path).AbsoluteUri + "#section";
                Assert.NotNull(destinationBrowser);
                Assert.NotNull(destinationModel);
                Assert.Equal(expected, destinationBrowser.Source!.AbsoluteUri);
                Assert.Equal(expected, destinationModel.SourceUri!.AbsoluteUri);
                Assert.Equal(expected, new Uri(destinationModel.AddressBarUrl).AbsoluteUri);
                var actualLocation = await destinationBrowser.InvokeScript("window.location.href");
                Assert.Equal(expected, JsonSerializer.Deserialize<string>(actualLocation!));
                Assert.Equal(pane.Id, main.FindWorkspacePaneIdForTab(destinationModel.Id));
                Assert.Equal(pane.Tabs.IndexOf(tab) + 1, pane.Tabs.IndexOf(destinationModel));
            }
            finally
            {
                ((INotifyCollectionChanged)pane.Tabs).CollectionChanged -= OnTabsChanged;
                window.Close();
                File.Delete(path);
                Directory.Delete(directory);
            }
        });

    private sealed class UnusedExecutorSelector : ITrustedExecutorSelector
    {
        public ITrustedExecutor SelectExecutor(TrustProfile trustProfile, string targetClientInstance)
            => throw new NotSupportedException("Document navigation must not select an executor.");
    }
}

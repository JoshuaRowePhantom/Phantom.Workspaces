using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowTabCommandsIntegrationTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task RestartSession_ContextMenuTargetsInactiveTab_RestartsClickedSession()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var restartService = new RecordingRestartService();
        viewModel.AgentSessionRestartService = restartService;
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));

        var firstTab = CreateSessionTab("session-a");
        var secondTab = CreateSessionTab("session-b");
        await viewModel.OpenTabAsync(firstTab);
        await viewModel.OpenTabAsync(secondTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var contentTabStrip = await WaitForContentTabStripAsync(window);
            var inactiveTabItem = contentTabStrip
                .GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Single(item => item.DataContext is WorkspaceDocument { Id: "tab-session-a" });
            var menu = Assert.IsType<ContextMenu>(inactiveTabItem.DocumentContextMenu);
            var factory = new WorkspaceDocumentMenuFactory();
            factory.Apply(menu, inactiveTabItem);
            var restartItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Restart agent session…"));
            Assert.NotNull(restartItem.Command);

            restartItem.Command!.Execute(null);
            var request = await restartService.LastRequest.Task;

            Assert.Equal(firstTab.AgentSessionId, request.SessionId);
            Assert.NotEqual(secondTab.AgentSessionId, request.SessionId);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    private static async Task<DocumentTabStrip> WaitForContentTabStripAsync(Window window)
    {
        var completion = new TaskCompletionSource<DocumentTabStrip>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? updated = null;
        updated = (_, _) =>
        {
            var strip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(candidate => candidate.DataContext is WorkspaceContentDock);
            if (strip is null)
            {
                return;
            }

            var items = strip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
            if (items.Count == 0)
            {
                return;
            }

            window.LayoutUpdated -= updated;
            completion.TrySetResult(strip);
        };

        window.LayoutUpdated += updated;
        window.UpdateLayout();
        updated(null, EventArgs.Empty);
        return await completion.Task;
    }

    private static AgentSessionWorkspaceTabViewModel CreateSessionTab(string sessionId)
    {
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = $"tab-{sessionId}",
            Title = sessionId,
            Entity = CreateWorkspaceEntity(sessionId),
            AgentSessionId = sessionId,
        };
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(sessionId), CreateChat(), () => ValueTask.CompletedTask));
        return tab;
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity(string id)
        => new(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString("N")),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""
                {
                  "entity-id": "{{id}}",
                  "display-name": { "default": "{{id}}" },
                  "entity-types": ["entity", "workspace"]
                }
                """).RootElement.Clone(),
            Relationships = [],
        });

    private static IAgentChat CreateChat()
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.HistoryPopulated).Returns(Task.CompletedTask);
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 1,
            Queues = ImmutableArray<AgentInputQueueSnapshot>.Empty,
        });
        chat.SetupGet(value => value.InputQueues).Returns(queues.Object);
        return chat.Object;
    }

    private sealed class RecordingRestartService : IAgentSessionRestartService
    {
        public TaskCompletionSource<AgentSessionRestartRequest> LastRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentSessionRestartAvailability GetAvailability(
            AgentSessionWorkspaceTabViewModel tab,
            TabCommandContext context)
            => new()
            {
                IsAvailable = true,
            };

        public Task<AgentSessionRestartResult> RestartAsync(
            AgentSessionRestartRequest request,
            CancellationToken cancellationToken)
        {
            this.LastRequest.TrySetResult(request);
            return Task.FromResult(new AgentSessionRestartResult
            {
                Outcome = AgentSessionRestartOutcome.Succeeded,
            });
        }
    }

    private sealed class DelegateConsentProvider(
        Func<string, CancellationToken, Task<bool>> confirmAsync) : IAgentSessionRestartConsentProvider
    {
        public Task<bool> ConfirmAsync(string sessionName, CancellationToken cancellationToken)
            => confirmAsync(sessionName, cancellationToken);
    }
}

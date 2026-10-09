using System.Collections.Specialized;
using System.Threading.Channels;
using AgentSchema;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelHostedSubAgentTests
{
    [AvaloniaFact]
    public async Task UnresolvedChildLink_ShowsExplicitFailureInsteadOfGenericBrowser()
    {
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var parent = await CreateChatAsync("parent", scheduler);
        using var logs = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(parent, "parent", "", logs, scheduler);

        view.NavigateToAgentHandler!("unresolved-child");

        Assert.IsType<AgentChatPlaceholderDetailViewModel>(view.SelectedDetailDocument!.DetailContent);
        Assert.Equal("sub-agent-unresolved-child", view.SelectedEditorItem!.Id);
        view.NavigateToAgentHandler!("another-unresolved-child");
        Assert.Single(view.AllDetailContents, item => item.Content is AgentChatPlaceholderDetailViewModel);

        await using var child = await CreateChatAsync("late-child", scheduler, hosted: true);
        view.NavigateToAgentHandler!(child.AgentId);
        await RegisterAsync(parent, child);
        Assert.Same(Assert.Single(view.SubAgentsContainer.Slots).SubAgentViewModel.ConversationDetail,
            view.SelectedDetailDocument!.DetailContent);
        Assert.DoesNotContain(view.AllDetailContents, item => item.Content is AgentChatPlaceholderDetailViewModel);
    }

    [AvaloniaFact]
    public async Task BackgroundTaskRouter_RealFactoryDescriptor_RunningLinkOpensHostedTranscript()
    {
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var parent = await CreateChatAsync("parent", scheduler);
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), new AgentServices(), scheduler);
        using var logs = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(parent, "parent", "", logs, scheduler);
        var router = new CopilotSubAgentRouter(
            Channel.CreateUnbounded<ChatResponseUpdate>().Writer, factory, parent);
        var registered = new TaskCompletionSource<SubAgent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = (INotifyCollectionChanged)parent.SubAgents;
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
        {
            if (parent.SubAgents.OfType<SubAgent>().FirstOrDefault() is { } child)
                registered.TrySetResult(child);
        }
        source.CollectionChanged += OnChanged;
        try
        {
            await router.RouteAsync(new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("task-call", "task", new Dictionary<string, object?>
                    {
                        ["mode"] = "background",
                        ["name"] = "release-candidate-gate",
                        ["description"] = "Verify release",
                        ["prompt"] = "Run targeted validation",
                    }),
                ],
            });
            var child = await registered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("release-candidate-gate", ((IRunningSubAgent)child).Name);
            Assert.False(child.AgentChat!.AcceptsUserInput);
            var display = Assert.Single(view.SubAgentDisplays);
            var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml(view.SubAgentDisplays, []);
            Assert.Contains($"data-navigate-agent-id=\"{display.AgentId}\"", html);
            view.NavigateToAgentHandler!(display.AgentId);
            var childView = Assert.Single(view.SubAgentsContainer.Slots).SubAgentViewModel;
            Assert.Same(child.AgentChat, childView.AgentChat);
            Assert.Same(childView.ConversationDetail, view.SelectedDetailDocument!.DetailContent);
            var transcript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var history = (INotifyCollectionChanged)child.AgentChat.History;
            void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
            {
                if (child.AgentChat.History.Any(item => item.Contents.OfType<TextContent>()
                        .Any(content => content.Text == "release validation transcript")))
                    transcript.TrySetResult();
            }
            history.CollectionChanged += OnHistoryChanged;
            try
            {
                await router.RouteAsync(new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new TextContent("release validation transcript")
                        {
                            AdditionalProperties = new()
                            {
                                [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = "native-agent",
                                [CopilotSdkStreamAdapter.SourceToolCallIdPropertyName] = "task-call",
                            },
                        },
                    ],
                });
                ((ICopilotSubAgentReceiver)child.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!).Complete();
                await transcript.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.DoesNotContain(parent.History, item => item.Contents.OfType<TextContent>()
                    .Any(content => content.Text == "release validation transcript"));
            }
            finally
            {
                history.CollectionChanged -= OnHistoryChanged;
            }
        }
        finally
        {
            source.CollectionChanged -= OnChanged;
            await router.DisposeRemainingLeasesAsync();
        }
    }

    [AvaloniaFact]
    public async Task NativeChild_SessionAliasNavigatesToCanonicalRunningLink()
    {
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var parent = await CreateChatAsync("parent", scheduler);
        using var logs = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(parent, "parent", "", logs, scheduler);
        var definition = CopilotSubAgentDefinitionDefaults.Create(
            subAgentSessionId: "native-child", displayName: "release-candidate-gate",
            description: "Verify release", name: "release-candidate-gate");
        await parent.GetOrCreateAsync("native-child", definition, "task-call");
        var child = (AgentChat)Assert.Single(parent.SubAgents);

        view.NavigateToAgentHandler!(child.AgentSessionId);

        Assert.Same(Assert.Single(view.SubAgentsContainer.Slots).SubAgentViewModel.ConversationDetail,
            view.SelectedDetailDocument!.DetailContent);
        Assert.Equal(child.AgentId, child.Information.AgentId);
    }

    [AvaloniaFact]
    public async Task HostedSdkChild_WithNestedRegisteredWrapper_RunningLinkOpensOwnTranscript()
    {
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var parent = await CreateChatAsync("parent", scheduler);
        await using var child = await CreateChatAsync("release-candidate-gate", scheduler, hosted: true);
        await using var nested = await CreateChatAsync("nested-verification", scheduler, hosted: true);
        await RegisterAsync(child, nested);
        await RegisterAsync(parent, child);
        child.SetCompletionState(AgentChatCompletionState.Running);
        nested.SetCompletionState(AgentChatCompletionState.Running);
        using var logs = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(parent, "parent", "", logs, scheduler);

        var display = Assert.Single(view.SubAgentDisplays);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml(view.SubAgentDisplays, []);
        Assert.Contains($"data-navigate-agent-id=\"{display.AgentId}\"", html);
        view.NavigateToAgentHandler!(display.AgentId);
        var childView = Assert.Single(view.SubAgentsContainer.Slots).SubAgentViewModel;
        Assert.Same(childView.ConversationDetail, view.SelectedDetailDocument!.DetailContent);

        view.NavigateToAgentHandler!(Assert.Single(display.SubAgents).AgentId);
        var nestedView = Assert.Single(childView.SubAgentsContainer.Slots).SubAgentViewModel;
        Assert.Same(nestedView.ConversationDetail, view.SelectedDetailDocument!.DetailContent);
        nested.SetCompletionState(AgentChatCompletionState.Succeeded);
        view.NavigateToAgentHandler!(nested.AgentSessionId);
        Assert.Same(nestedView.ConversationDetail, view.SelectedDetailDocument!.DetailContent);
    }

    [AvaloniaFact]
    public async Task HostedSdkWrapper_BrowserRendersNameAndCommandOpensTranscript()
    {
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var parent = await CreateChatAsync("parent", scheduler);
        await using var child = await CreateChatAsync("release-candidate-gate", scheduler, hosted: true);
        await RegisterAsync(parent, child);
        using var logs = new ObservableLoggerFactory();
        await using var view = new AgentViewModel(parent, "parent", "", logs, scheduler);
        view.SelectedEditorItem = view.EditorItems.Single().Children.Single(item => item.Id == "chat-sub-agents");
        var control = new AgentChatEditorControl { DataContext = view };
        var browser = view.SubAgentsContainer.Browser;
        var template = control.DataTemplates.Single(item => item.Match(browser));
        var browserControl = template.Build(browser)!;
        browserControl.DataContext = browser;
        var window = new Window { Width = 1000, Height = 700, Content = browserControl };
        try
        {
            window.Show();
            window.UpdateLayout();
            var card = browserControl.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Command == browser.OpenChildCommand);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "release-candidate-gate");
            Assert.Equal(child.AgentId, card.CommandParameter);
            card.Command!.Execute(card.CommandParameter);
            Assert.Same(Assert.Single(view.SubAgentsContainer.Slots).SubAgentViewModel.ConversationDetail,
                view.SelectedDetailDocument!.DetailContent);
        }
        finally
        {
            window.Close();
        }
    }

    private static Task<AgentChat> CreateChatAsync(string name, TaskScheduler scheduler, bool hosted = false)
        => AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = hosted
                ? CopilotSubAgentDefinitionDefaults.Create(
                    subAgentSessionId: name, displayName: name, description: "Verify release", name: name)
                : AgentDefinitionLoader.LoadAgentFromJson(
                    """{"kind":"prompt","name":"parent","model":{"id":"test","provider":"echo","apiType":"Echo"},"tools":[]}"""),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ForegroundScheduler = scheduler,
            DisplayNameOverride = name,
        });

    private static async Task RegisterAsync(AgentChat parent, AgentChat child)
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = (INotifyCollectionChanged)parent.SubAgents;
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
        {
            if (parent.SubAgents.OfType<SubAgent>().Any(agent => agent.SessionId.Value == child.AgentSessionId))
                registered.TrySetResult();
        }
        source.CollectionChanged += OnChanged;
        try
        {
            await ((ISubAgentTable)parent).Add(child);
            await registered.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            source.CollectionChanged -= OnChanged;
        }
    }
}

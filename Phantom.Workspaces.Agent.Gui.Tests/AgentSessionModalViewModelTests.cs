using System.Reflection;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Moq;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentSessionModalViewModelTests
{
    [Fact]
    public void PublicSurface_ExposesImmutableModalValuesAndAsyncResponse()
    {
        var type = typeof(AgentSessionModalViewModel);

        Assert.True(type.IsSealed);
        Assert.All(
            new[]
            {
                nameof(AgentSessionModalViewModel.Id),
                nameof(AgentSessionModalViewModel.Title),
                nameof(AgentSessionModalViewModel.Body),
                nameof(AgentSessionModalViewModel.Content),
            },
            name => Assert.False(type.GetProperty(name)!.CanWrite));
        Assert.Equal(typeof(Task), type.GetMethod(nameof(AgentSessionModalViewModel.RespondAsync))!.ReturnType);
    }

    [Fact]
    public void ModalStackControl_IsSealedAndAddsNoPublicOperations()
    {
        var methods = typeof(AgentSessionModalStackControl)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.True(typeof(AgentSessionModalStackControl).IsSealed);
        Assert.All(methods, method => Assert.Equal("InitializeComponent", method.Name));
    }

    [AvaloniaFact]
    public async Task ModalStackControl_ApprovalButton_SubmitsAndStaysPendingUntilOwnerDismisses()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                    """),
            });
        await using var agent = new AgentViewModel(
            chat,
            "Agent",
            string.Empty,
            loggerFactory,
            TaskScheduler.Default);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) => published.TrySetResult();
        chat.PublishModal(new AgentChatModal
        {
            Id = "approval",
            OwnerAgentId = "owner",
            Title = "Approve action",
            Body = "Continue?",
            Content = new ApprovalModalContent
            {
                ApproveLabel = "Approve",
                RejectLabel = "Reject",
            },
        });
        await published.Task.WaitAsync(TestContext.Current.CancellationToken);
        var control = new AgentSessionModalStackControl { DataContext = agent };
        var window = new Window { Content = control };
        window.Show();
        var approve = control.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => Equals(button.Content, "Approve"));

        approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(approve.IsEnabled);
        Assert.Single(agent.Modals);

        var secondPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) =>
        {
            if (chat.Modals.Count == 2)
            {
                secondPublished.TrySetResult();
            }
        };
        chat.PublishModal(Modal("second", new FreeformModalContent { IsRequired = false }));
        await secondPublished.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(
            approve,
            control.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Approve")));
        Assert.False(approve.IsEnabled);

        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) =>
        {
            if (chat.Modals.All(modal => modal.Id != "approval"))
            {
                dismissed.TrySetResult();
            }
        };
        chat.PublishModalDismiss("approval");
        await dismissed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(agent.Modals, modal => modal.Id == "approval");
        Assert.Equal("second", Assert.Single(agent.Modals).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ModalStackControl_RendersAccessibleInputsForEveryContentKind()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                    """),
            });
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) =>
        {
            if (chat.Modals.Count == 4)
            {
                published.TrySetResult();
            }
        };
        chat.PublishModal(Modal("freeform", new FreeformModalContent
        {
            IsRequired = true,
            Placeholder = "Explain",
        }));
        chat.PublishModal(Modal("single", new MultipleChoiceModalContent
        {
            AllowsMultiple = false,
            Options = [JsonSerializer.SerializeToElement("One"), JsonSerializer.SerializeToElement("Two")],
        }));
        chat.PublishModal(Modal("multiple", new MultipleChoiceModalContent
        {
            AllowsMultiple = true,
            Options = [JsonSerializer.SerializeToElement("A"), JsonSerializer.SerializeToElement("B")],
        }));
        chat.PublishModal(Modal("approval", new ApprovalModalContent
        {
            ApproveLabel = "Approve",
            RejectLabel = "Reject",
        }));
        await published.Task.WaitAsync(TestContext.Current.CancellationToken);
        await using var agent = new AgentViewModel(
            chat,
            "Agent",
            string.Empty,
            loggerFactory,
            TaskScheduler.Default);
        var control = new AgentSessionModalStackControl { DataContext = agent };
        var window = new Window { Content = control };
        window.Show();

        var textInput = Assert.Single(control.GetVisualDescendants().OfType<TextBox>());
        Assert.Equal("Explain", textInput.PlaceholderText);
        Assert.False(control.GetVisualDescendants().OfType<Button>()
            .First(button => Equals(button.Content, "Submit")).IsEnabled);
        Assert.Equal(2, control.GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "One"));
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Approve"));
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Reject"));

        textInput.Text = "draft response";
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) =>
        {
            if (chat.Modals.Count == 5)
            {
                added.TrySetResult();
            }
        };
        chat.PublishModal(Modal("another", new ApprovalModalContent
        {
            ApproveLabel = "Continue",
            RejectLabel = "Cancel",
        }));
        await added.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(textInput, control.GetVisualDescendants().OfType<TextBox>().Single());
        Assert.Equal("draft response", textInput.Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task ModalStackControl_ProtocolDisconnect_ReenablesResponseControls()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var source = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                    """),
            });
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)source.Modals).CollectionChanged += (_, _) => published.TrySetResult();
        source.PublishModal(Modal("approval", new ApprovalModalContent
        {
            ApproveLabel = "Approve",
            RejectLabel = "Reject",
        }));
        await published.Task.WaitAsync(TestContext.Current.CancellationToken);
        var chat = CreateDelegatingChat(source);
        chat.Setup(value => value.RespondToModalAsync(
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteAgentProtocolException("The remote channel closed."));
        await using var agent = new AgentViewModel(
            chat.Object,
            "Agent",
            string.Empty,
            loggerFactory,
            TaskScheduler.Default);
        var control = new AgentSessionModalStackControl { DataContext = agent };
        var window = new Window { Content = control };
        window.Show();
        var approve = control.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => Equals(button.Content, "Approve"));

        approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(approve.IsEnabled);
        Assert.Contains(
            control.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsVisible
                && string.Equals(
                    text.Text,
                    "The response could not be sent. Try again.",
                    StringComparison.Ordinal));
        Assert.Single(agent.Modals);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ModalStackControl_DetachDuringResponse_DoesNotSurfaceLifetimeCancellation()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var source = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                    """),
            });
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)source.Modals).CollectionChanged += (_, _) => published.TrySetResult();
        source.PublishModal(Modal("approval", new ApprovalModalContent
        {
            ApproveLabel = "Approve",
            RejectLabel = "Reject",
        }));
        await published.Task.WaitAsync(TestContext.Current.CancellationToken);
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = CreateDelegatingChat(source);
        chat.Setup(value => value.RespondToModalAsync(
                It.IsAny<string>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, JsonElement _, CancellationToken ct) =>
                WaitUntilCancelledAsync(responseStarted, ct));
        await using var agent = new AgentViewModel(
            chat.Object,
            "Agent",
            string.Empty,
            loggerFactory,
            TaskScheduler.Default);
        var control = new AgentSessionModalStackControl { DataContext = agent };
        var window = new Window { Content = control };
        window.Show();
        var responseControl = new Button { IsEnabled = true };
        var error = new TextBlock { IsVisible = false };
        var respond = typeof(AgentSessionModalStackControl).GetMethod(
            "RespondAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var responseTask = Assert.IsAssignableFrom<Task>(respond.Invoke(
            control,
            [
                Assert.Single(agent.Modals),
                JsonSerializer.SerializeToElement(true),
                error,
                new Control[] { responseControl },
            ]));
        await responseStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        window.Close();
        await responseTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(error.IsVisible);
        Assert.False(responseControl.IsEnabled);
    }

    [AvaloniaFact]
    public async Task EquivalentMultipleChoiceSnapshot_PreservesModalViewModelAndSelections()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                    """),
            });
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += (_, _) =>
            firstPublished.TrySetResult();
        chat.PublishModal(MultipleChoiceModal("choice"));
        await firstPublished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await using var agent = new AgentViewModel(
            chat,
            "Agent",
            string.Empty,
            loggerFactory,
            TaskScheduler.Default);
        var original = Assert.Single(agent.Modals);
        var control = new AgentSessionModalStackControl { DataContext = agent };
        var window = new Window { Content = control };
        window.Show();
        var selection = control.GetVisualDescendants()
            .OfType<CheckBox>()
            .First();
        selection.IsChecked = true;
        var source = Assert.IsType<System.Collections.ObjectModel.ObservableCollection<AgentChatModal>>(
            typeof(AgentChat)
                .GetField("modals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(chat));
        source[0] = MultipleChoiceModal("choice");

        Assert.Same(original, Assert.Single(agent.Modals));
        Assert.Same(selection, control.GetVisualDescendants().OfType<CheckBox>().First());
        Assert.True(selection.IsChecked);
        window.Close();
    }

    private static Mock<IAgentChat> CreateDelegatingChat(IAgentChat source)
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.Information).Returns(() => source.Information);
        chat.SetupGet(value => value.Usage).Returns(() => source.Usage);
        chat.SetupGet(value => value.IsBusy).Returns(() => source.IsBusy);
        chat.SetupGet(value => value.History).Returns(source.History);
        chat.SetupGet(value => value.HistoryPopulated).Returns(source.HistoryPopulated);
        chat.SetupGet(value => value.RunningItems).Returns(source.RunningItems);
        chat.SetupGet(value => value.InputQueues).Returns(source.InputQueues);
        chat.SetupGet(value => value.SubAgents).Returns(source.SubAgents);
        chat.SetupGet(value => value.Modals).Returns(source.Modals);
        chat.SetupGet(value => value.SlashCommands).Returns(source.SlashCommands);
        chat.Setup(value => value.GetToolSnapshot()).Returns(() => source.GetToolSnapshot());
        chat.Setup(value => value.GetService(It.IsAny<Type>()))
            .Returns((Type serviceType) => source.GetService(serviceType));
        chat.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return chat;
    }

    private static async Task WaitUntilCancelledAsync(
        TaskCompletionSource started,
        CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(cancelled.SetResult);
        started.SetResult();
        await cancelled.Task;
        ct.ThrowIfCancellationRequested();
    }

    private static AgentChatModal Modal(string id, AgentChatModalContent content) => new()
    {
        Id = id,
        OwnerAgentId = "owner",
        Title = id,
        Body = $"{id} body",
        Content = content,
    };

    private static AgentChatModal MultipleChoiceModal(string id) =>
        Modal(id, new MultipleChoiceModalContent
        {
            AllowsMultiple = true,
            Options =
            [
                JsonSerializer.SerializeToElement("One"),
                JsonSerializer.SerializeToElement("Two"),
            ],
        });
}

using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSlashCommandTests
{
    [Fact]
    public async Task ConfigureSlashCommands_HandlerCompletions_AreSortedByLabelIgnoringCase()
    {
        // Arrange — register a fake handler that returns unsorted completions.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Register a fake handler after ConfigureSlashCommands so it is visible in the registry.
        var fakeHandler = new FakeUnsortedCompletionsHandler("fake-cmd");
        chat.SlashCommands.Register(fakeHandler);

        // Act — invoke the completions provider directly with the fake command name.
        var provider = viewModel.InputQueue!.DefaultComposer.SlashCompletionsProviderAsync!;
        var completions = await provider("fake-cmd", string.Empty, CancellationToken.None);

        // Assert — results must be alphabetically sorted by Label, case-insensitively.
        Assert.Equal(3, completions.Count);
        Assert.Equal("/alpha", completions[0].Label);
        Assert.Equal("/Beta", completions[1].Label);
        Assert.Equal("/Gamma", completions[2].Label);
    }

    [Fact]
    public async Task ConfigureSlashCommands_DoesNotRegisterDiagnosticsCommand()
    {
        // Arrange — verify that /diagnostics command is not registered
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — check that /diagnostics is not in the commands list
        var commands = chat.SlashCommands.Commands;

        // Assert — /diagnostics should not be present
        Assert.DoesNotContain(commands, cmd => cmd.Name.Equals("diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    // ── Issue #332: /help command routing tests ───────────────────────────────

    [Fact]
    public async Task RunSlashCommandAsync_HelpCommand_EnqueuesWithHelpRole()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — run /help command via the SlashCommandInterceptor
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/help");

        // Wait for the command to enqueue the help item (runs on foreground scheduler)
        await Task.Delay(500, TestContext.Current.CancellationToken); // Give the async operation time to complete

        // Assert — history should contain an item with HelpChatRole
        var helpItem = chat.History.FirstOrDefault(item => item.Role == AgentChatHistoryItem.HelpChatRole);
        Assert.NotNull(helpItem);
        Assert.NotEqual(AgentChatHistoryItem.DiagnosticChatRole, helpItem.Role);
    }

    [Fact]
    public async Task RunSlashCommandAsync_OtherCommand_EnqueuesWithDiagnosticRole()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // Register a test command that returns a diagnostic status message
        chat.SlashCommands.Register(new FakeDiagnosticCommandHandler());
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — run /testdiag command via the SlashCommandInterceptor
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/testdiag");

        // Wait for the command to enqueue the diagnostic item (runs on foreground scheduler)
        await Task.Delay(500, TestContext.Current.CancellationToken); // Give the async operation time to complete

        // Assert — history should contain an item with DiagnosticChatRole, not HelpChatRole
        var diagnosticItem = chat.History.FirstOrDefault(item => item.Role == AgentChatHistoryItem.DiagnosticChatRole);
        Assert.NotNull(diagnosticItem);
        Assert.DoesNotContain(chat.History, item => item.Role == AgentChatHistoryItem.HelpChatRole);
    }

    // ── Issue #1396: transient slash-command results are shown as non-persisted diagnostics ──

    [Fact]
    public async Task RunSlashCommand_ModelCommand_AddsDiagnosticNoteToHistory()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        chat.SlashCommands.Register(new FakeModelCommandHandler("echo-model"));
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — /model with an argument switches the model and returns a transient result.
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/model gpt-5");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole),
            "transient /model result to appear as a diagnostic note");

        // Assert — the result is visible in the transcript as a diagnostic note.
        var note = chat.History.Single(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole);
        Assert.Contains("Model set to: gpt-5", string.Concat(note.Contents.OfType<TextContent>().Select(c => c.Text)));
    }

    [Fact]
    public async Task RunSlashCommand_ModelCommandNoArgs_AddsDiagnosticNoteWithCurrentModel()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        chat.SlashCommands.Register(new FakeModelCommandHandler("echo-model"));
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — /model with no argument reports the current model as a transient result.
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/model");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole),
            "transient /model result to appear as a diagnostic note");

        var note = chat.History.Single(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole);
        Assert.Contains("Active model: echo-model", string.Concat(note.Contents.OfType<TextContent>().Select(c => c.Text)));
    }

    [Fact]
    public async Task RunSlashCommand_TransientResult_IsVisibleAsDiagnostic()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        chat.SlashCommands.Register(new FakeTransientCommandHandler());
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/transient-test");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole),
            "transient result to appear as a diagnostic note");

        // Assert — the transient result is visible as a diagnostic note (not silently discarded).
        var note = chat.History.Single(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole);
        Assert.Contains("Transient status", string.Concat(note.Contents.OfType<TextContent>().Select(c => c.Text)));
    }

    [Fact]
    public async Task RunSlashCommand_TransientResult_DoesNotRaiseDeadTransientNotification()
    {
        // Regression guard for issue #1396: transient slash-command results must be routed to the
        // visible diagnostic-note path, not silently discarded to a subscriber-less event. The
        // dead TransientNotification event has been removed; this test asserts the diagnostic-note
        // path is used and produces exactly one visible item.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        chat.SlashCommands.Register(new FakeTransientCommandHandler());
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/transient-test");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(i => i.Role == AgentChatHistoryItem.DiagnosticChatRole),
            "transient result to appear as a diagnostic note");

        // Assert — exactly one diagnostic note, nothing silently discarded.
        Assert.Single(chat.History);
        Assert.Equal(AgentChatHistoryItem.DiagnosticChatRole, chat.History[0].Role);
    }

    [Fact]
    public async Task RunSlashCommandAsync_ErrorResult_IsNotTransient()
    {
        // Arrange
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        chat.SlashCommands.Register(new FakeThrowingCommandHandler());
        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Act — the handler throws, so the result should be persisted (not transient)
        var interceptor = viewModel.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync!;
        await interceptor("/throw-test");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Count > 0,
            "error result to be added to history");

        // Assert — error should be added to history as a system note.
        Assert.NotEmpty(chat.History);
    }

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
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

    /// <summary>
    /// Fake handler that returns a diagnostic status message (non-help role).
    /// </summary>
    private sealed class FakeDiagnosticCommandHandler : ISlashCommandHandler
    {
        public string Name => "testdiag";
        public string Description => "Test diagnostic command.";
        public string? Usage => null;
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = "Test diagnostic message", IsTransient = false });

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context,
            string partialArguments,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
    }

    /// <summary>
    /// Fake handler that returns completions in deliberately unsorted order
    /// (Gamma before Beta before alpha) to verify that the caller sorts them.
    /// </summary>
    private sealed class FakeUnsortedCompletionsHandler : ISlashCommandHandler
    {
        public FakeUnsortedCompletionsHandler(string name) => this.Name = name;

        public string Name { get; }
        public string Description => "Fake handler for sorting tests.";
        public string? Usage => null;
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = string.Empty });

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context,
            string partialArguments,
            CancellationToken cancellationToken)
        {
            // Intentionally unsorted: Gamma, Beta, alpha — to detect missing sort.
            IReadOnlyList<SlashCommandCompletion> items =
            [
                new SlashCommandCompletion("Gamma ", "/Gamma", "desc"),
                new SlashCommandCompletion("Beta ", "/Beta", "desc"),
                new SlashCommandCompletion("alpha ", "/alpha", "desc"),
            ];
            return Task.FromResult(items);
        }
    }

    /// <summary>
    /// Fake handler that returns a transient (default) result.
    /// </summary>
    private sealed class FakeTransientCommandHandler : ISlashCommandHandler
    {
        public string Name => "transient-test";
        public string Description => "Returns a transient result.";
        public string? Usage => null;
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
            => Task.FromResult(new SlashCommandResult { StatusMessage = "Transient status" });

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context,
            string partialArguments,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
    }

    /// <summary>
    /// Fake handler that mimics the real /model handler: returns a transient result reporting or
    /// setting the model. Used to verify transient results render as non-persisted diagnostics.
    /// </summary>
    private sealed class FakeModelCommandHandler : ISlashCommandHandler
    {
        private readonly string currentModel;

        public FakeModelCommandHandler(string currentModel) => this.currentModel = currentModel;

        public string Name => "model";
        public string Description => "List or set the active model.";
        public string? Usage => "/model [model-id]";
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
        {
            var modelId = arguments.Trim();
            var message = string.IsNullOrEmpty(modelId)
                ? $"Active model: {this.currentModel}"
                : $"Model set to: {modelId}";
            return Task.FromResult(new SlashCommandResult { StatusMessage = message });
        }

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context,
            string partialArguments,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
    }

    /// <summary>
    /// Fake handler that throws to test error handling (errors should be persisted).
    /// </summary>
    private sealed class FakeThrowingCommandHandler : ISlashCommandHandler
    {
        public string Name => "throw-test";
        public string Description => "Always throws.";
        public string? Usage => null;
        public string? LongDescription => null;

        public Task<SlashCommandResult> ExecuteAsync(
            SlashCommandContext context,
            string arguments,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Deliberate test failure");

        public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
            SlashCommandContext context,
            string partialArguments,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
    }
}

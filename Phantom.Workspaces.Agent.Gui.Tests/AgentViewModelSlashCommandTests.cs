using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;
using System;
using System.Collections.Generic;
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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        viewModel.ConfigureSlashCommands(() => new SlashCommandContext { AgentChat = chat });

        // Register a fake handler after ConfigureSlashCommands so it is visible in the registry.
        var fakeHandler = new FakeUnsortedCompletionsHandler("fake-cmd");
        ((SlashCommandRegistry)chat.SlashCommands).Register(fakeHandler);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        // Register a test command that returns a diagnostic status message
        ((SlashCommandRegistry)chat.SlashCommands).Register(new FakeDiagnosticCommandHandler());
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
            => Task.FromResult(new SlashCommandResult { StatusMessage = "Test diagnostic message" });

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
}

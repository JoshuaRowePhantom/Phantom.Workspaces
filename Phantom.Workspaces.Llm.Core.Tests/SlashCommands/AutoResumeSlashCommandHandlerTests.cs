using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class AutoResumeSlashCommandHandlerTests
{
    private static readonly AgentDefinition EchoAgentDefinition =
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    private static Task<AgentChat> CreateChatAsync() =>
        AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
        });

    private readonly AutoResumeSlashCommandHandler handler = new();

    [Fact]
    public void Name_IsAutoResume()
    {
        Assert.Equal("auto-resume", this.handler.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(this.handler.Description));
    }

    [Fact]
    public void Usage_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(this.handler.Usage));
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateAutoResumeAsyncIsNull_ReturnsUnavailableMessage()
    {
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = ".",
        };

        var result = await this.handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("not persisted", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTrustedExecutorIdentifierIsNull_ReturnsUnavailableMessage()
    {
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            UpdateAutoResumeAsync = (_, _) => Task.CompletedTask,
        };

        var result = await this.handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("executor context", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArguments_WhenAutoResumeIsDisabled_EnablesWithDefaultPromptAndConfirms()
    {
        await using var chat = await CreateChatAsync();
        AutoResumeSettings? capturedSettings = null;
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = ".",
            CurrentAutoResume = null,
            UpdateAutoResumeAsync = (settings, _) =>
            {
                capturedSettings = settings;
                return Task.CompletedTask;
            },
        };

        var result = await this.handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.NotNull(capturedSettings);
        Assert.Equal(".", capturedSettings!.TrustedExecutor);
        Assert.Null(capturedSettings.ResumePrompt);
        Assert.Contains("enabled", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AutoResumeSlashCommandHandler.DefaultResumePrompt, result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArguments_WhenAutoResumeIsEnabled_DisablesAndConfirms()
    {
        await using var chat = await CreateChatAsync();
        var updateCalled = false;
        AutoResumeSettings? capturedSettings = new AutoResumeSettings { TrustedExecutor = "sentinel" };
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = ".",
            CurrentAutoResume = new AutoResumeSettings { TrustedExecutor = "." },
            UpdateAutoResumeAsync = (settings, _) =>
            {
                updateCalled = true;
                capturedSettings = settings;
                return Task.CompletedTask;
            },
        };

        var result = await this.handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.True(updateCalled);
        Assert.Null(capturedSettings);
        Assert.Contains("disabled", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithArguments_WhenAutoResumeIsDisabled_EnablesWithCustomPrompt()
    {
        await using var chat = await CreateChatAsync();
        AutoResumeSettings? capturedSettings = null;
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = ".",
            CurrentAutoResume = null,
            UpdateAutoResumeAsync = (settings, _) =>
            {
                capturedSettings = settings;
                return Task.CompletedTask;
            },
        };

        var result = await this.handler.ExecuteAsync(
            context,
            "Resume the build process where you left off.",
            CancellationToken.None);

        Assert.NotNull(capturedSettings);
        Assert.Equal(".", capturedSettings!.TrustedExecutor);
        Assert.Equal("Resume the build process where you left off.", capturedSettings.ResumePrompt);
        Assert.Contains("enabled", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resume the build process where you left off.", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithArguments_WhenAutoResumeIsEnabled_UpdatesResumePrompt()
    {
        await using var chat = await CreateChatAsync();
        AutoResumeSettings? capturedSettings = null;
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = ".",
            CurrentAutoResume = new AutoResumeSettings { TrustedExecutor = ".", ResumePrompt = "Old prompt" },
            UpdateAutoResumeAsync = (settings, _) =>
            {
                capturedSettings = settings;
                return Task.CompletedTask;
            },
        };

        var result = await this.handler.ExecuteAsync(
            context,
            "New custom prompt",
            CancellationToken.None);

        Assert.NotNull(capturedSettings);
        Assert.Equal("New custom prompt", capturedSettings!.ResumePrompt);
        Assert.Contains("enabled", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithArguments_UsesTrustedExecutorIdentifierFromContext()
    {
        await using var chat = await CreateChatAsync();
        AutoResumeSettings? capturedSettings = null;
        var remoteExecutorId = "12345678-1234-1234-1234-123456789012";
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            TrustedExecutorIdentifier = remoteExecutorId,
            CurrentAutoResume = null,
            UpdateAutoResumeAsync = (settings, _) =>
            {
                capturedSettings = settings;
                return Task.CompletedTask;
            },
        };

        await this.handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal(remoteExecutorId, capturedSettings?.TrustedExecutor);
    }
}

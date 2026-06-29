using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class CopilotSdkWorkingDirectorySlashCommandHandlerTests
{
    private static CopilotSdkChatClient CreateClient() =>
        new("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

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

    [Fact]
    public void Name_IsWorkingDirectory()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);

        Assert.Equal("working-directory", handler.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_AndNoParameterValues_ReturnsNotSetMessage()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await handler.ExecuteAsync(context, arguments: string.Empty, CancellationToken.None);

        Assert.Contains("not set", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_AndParameterValues_ReturnsCurrentDirectory()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            CurrentParameterValues = new Dictionary<string, string> { ["working-directory"] = @"C:\Projects\Foo" },
        };

        var result = await handler.ExecuteAsync(context, arguments: string.Empty, CancellationToken.None);

        Assert.Contains(@"C:\Projects\Foo", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentPath_ReturnsErrorStatus()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await handler.ExecuteAsync(
            context,
            arguments: @"C:\DoesNotExist_XYZ_99999",
            CancellationToken.None);

        Assert.Contains("does not exist", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentPath_DoesNotUpdateLiveClient()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };

        await handler.ExecuteAsync(context, arguments: @"C:\DoesNotExist_XYZ_99999", CancellationToken.None);

        Assert.Null(client.WorkingDirectoryOverride);
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingPath_AndNullUpdateCallback_UpdatesLiveClient()
    {
        var existingPath = Path.GetTempPath();
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();

        // No UpdateParameterValuesAsync — previously this would return an error, now it should succeed.
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await handler.ExecuteAsync(context, arguments: existingPath, CancellationToken.None);

        Assert.Contains(existingPath, result.StatusMessage);
        Assert.False(result.StatusMessage.Contains("Cannot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existingPath, client.WorkingDirectoryOverride);
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingPath_AndUpdateCallback_UpdatesLiveClientAndPersists()
    {
        var existingPath = Path.GetTempPath();
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        IReadOnlyDictionary<string, string>? captured = null;
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            UpdateParameterValuesAsync = (values, _) =>
            {
                captured = values;
                return Task.CompletedTask;
            },
        };

        var result = await handler.ExecuteAsync(context, arguments: existingPath, CancellationToken.None);

        Assert.Contains(existingPath, result.StatusMessage);
        Assert.Equal(existingPath, client.WorkingDirectoryOverride);
        Assert.NotNull(captured);
        Assert.Equal(existingPath, captured!["working-directory"]);
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingPath_PreservesOtherParameterValues()
    {
        var existingPath = Path.GetTempPath();
        using var client = CreateClient();
        var handler = new CopilotSdkWorkingDirectorySlashCommandHandler(client);
        await using var chat = await CreateChatAsync();
        IReadOnlyDictionary<string, string>? captured = null;
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            CurrentParameterValues = new Dictionary<string, string>
            {
                ["other-param"] = "other-value",
                ["working-directory"] = "old-value",
            },
            UpdateParameterValuesAsync = (values, _) =>
            {
                captured = values;
                return Task.CompletedTask;
            },
        };

        await handler.ExecuteAsync(context, arguments: existingPath, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("other-value", captured!["other-param"]);
        Assert.Equal(existingPath, captured["working-directory"]);
    }
}

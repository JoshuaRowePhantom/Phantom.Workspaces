using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class WorkingDirectorySlashCommandHandlerTests
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

    private readonly WorkingDirectorySlashCommandHandler handler = new();

    [Fact]
    public void Name_IsWorkingDirectory()
    {
        Assert.Equal("working-directory", this.handler.Name);
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

    [AvaloniaFact]
    public async Task ExecuteAsync_WithNoArgument_AndNoParameterValues_ReturnsNotSetMessage()
    {
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await this.handler.ExecuteAsync(context, arguments: string.Empty, CancellationToken.None);

        Assert.Contains("not set", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RequiresAgentRecreation);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithNoArgument_AndParameterValues_ReturnsCurrentDirectory()
    {
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext
        {
            AgentChat = chat,
            CurrentParameterValues = new Dictionary<string, string> { ["working-directory"] = @"C:\Projects\Foo" },
        };

        var result = await this.handler.ExecuteAsync(context, arguments: string.Empty, CancellationToken.None);

        Assert.Contains(@"C:\Projects\Foo", result.StatusMessage);
        Assert.False(result.RequiresAgentRecreation);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithoutUpdateCallback_ReturnsErrorStatus()
    {
        await using var chat = await CreateChatAsync();
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await this.handler.ExecuteAsync(context, arguments: @"C:\SomePath", CancellationToken.None);

        Assert.Contains("not persisted", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RequiresAgentRecreation);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithNonExistentPath_ReturnsErrorStatus()
    {
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

        var result = await this.handler.ExecuteAsync(
            context,
            arguments: @"C:\DoesNotExist_XYZ_99999",
            CancellationToken.None);

        Assert.Contains("does not exist", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RequiresAgentRecreation);
        Assert.Null(captured);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithExistingPath_UpdatesParameterValuesAndRequiresRecreation()
    {
        var existingPath = Path.GetTempPath();
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

        var result = await this.handler.ExecuteAsync(context, arguments: existingPath, CancellationToken.None);

        Assert.True(result.RequiresAgentRecreation);
        Assert.Contains(existingPath, result.StatusMessage);
        Assert.NotNull(captured);
        Assert.Equal(existingPath, captured!["working-directory"]);
    }

    [AvaloniaFact]
    public async Task ExecuteAsync_WithExistingPath_PreservesOtherParameterValues()
    {
        var existingPath = Path.GetTempPath();
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

        await this.handler.ExecuteAsync(context, arguments: existingPath, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("other-value", captured!["other-param"]);
        Assert.Equal(existingPath, captured["working-directory"]);
    }
}

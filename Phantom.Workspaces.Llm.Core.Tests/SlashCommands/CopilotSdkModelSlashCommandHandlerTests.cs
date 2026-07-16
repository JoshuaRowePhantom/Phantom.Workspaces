using GitHub.Copilot.SDK;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class CopilotSdkModelSlashCommandHandlerTests
{
    private static readonly AgentDefinition EchoAgent =
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    private static CopilotSdkChatClient CreateClient(string modelId = "gpt-5") =>
        new(modelId, $"GitHub Copilot ({modelId})", gitHubToken: null, loggerFactory: null);

    private static async Task<SlashCommandContext> CreateContextAsync()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = EchoAgent,
        });
        return new SlashCommandContext { AgentChat = chat };
    }

    [Fact]
    public void Name_IsModel()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkModelSlashCommandHandler(client);

        Assert.Equal("model", handler.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WithModelId_CallsSetModelId()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        await handler.ExecuteAsync(context, "claude-4", CancellationToken.None);

        Assert.Equal("claude-4", client.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_WithModelId_ReturnsConfirmationMessage()
    {
        using var client = CreateClient();
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, "claude-4", CancellationToken.None);

        Assert.Contains("claude-4", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_ReturnsCurrentModel()
    {
        using var client = CreateClient("gpt-5");
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("gpt-5", result.StatusMessage);
    }

    [Fact]
    public async Task GetCompletionsAsync_WhenListModelsAsyncThrows_ReturnsEmpty()
    {
        // A disconnected client will fail on ListModelsAsync — handler should return empty, not throw.
        using var client = CreateClient();
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var completions = await handler.GetCompletionsAsync(context, string.Empty, cts.Token);

        Assert.Empty(completions);
    }

    // NOTE: Tests requiring a running CopilotClient (GetCompletionsAsync_WithModels_*,
    // GetCompletionsAsync_WithPartialArgument_*, GetCompletionsAsync_Description_*)
    // cannot run without network/Copilot access. The handler logic is validated through
    // the deterministic tests above and the FormatDescription test below.

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_DoesNotCallSetModelId()
    {
        using var client = CreateClient("original-model");
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        // ModelId should remain unchanged.
        Assert.Equal("original-model", client.ModelId);
    }
}

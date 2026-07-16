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

    /// <summary>
    /// Deterministic in-memory test double for <see cref="IModelSlashCommandClient"/>.
    /// Lets tests control the model list, simulate a failing <c>ListModelsAsync</c>,
    /// and observe <c>SetModelId</c> calls without any Copilot connectivity.
    /// </summary>
    private sealed class FakeModelClient : IModelSlashCommandClient
    {
        private readonly IReadOnlyList<ModelInfo>? models;
        private readonly bool throwOnList;

        public FakeModelClient(IReadOnlyList<ModelInfo>? models = null, bool throwOnList = false, string modelId = "gpt-5")
        {
            this.models = models;
            this.throwOnList = throwOnList;
            this.ModelId = modelId;
        }

        public string ModelId { get; private set; }

        public void SetModelId(string modelId) => this.ModelId = modelId;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            if (this.throwOnList)
            {
                throw new InvalidOperationException("Simulated Copilot connection failure.");
            }

            return Task.FromResult(this.models ?? Array.Empty<ModelInfo>());
        }
    }

    private static ModelInfo Model(string id, string name, double? multiplier = null) =>
        new()
        {
            Id = id,
            Name = name,
            Billing = multiplier is { } m ? new ModelBilling { Multiplier = m } : null,
        };

    private static CopilotSdkChatClient CreateRealClient(string modelId = "gpt-5") =>
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
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient());

        Assert.Equal("model", handler.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WithModelId_CallsSetModelId()
    {
        var client = new FakeModelClient(modelId: "gpt-5");
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        await handler.ExecuteAsync(context, "claude-4", CancellationToken.None);

        Assert.Equal("claude-4", client.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_WithModelId_ReturnsConfirmationMessage()
    {
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient());
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, "claude-4", CancellationToken.None);

        Assert.Contains("claude-4", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_ReturnsCurrentModel()
    {
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(modelId: "gpt-5"));
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("gpt-5", result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoArgument_DoesNotCallSetModelId()
    {
        var client = new FakeModelClient(modelId: "original-model");
        var handler = new CopilotSdkModelSlashCommandHandler(client);
        var context = await CreateContextAsync();

        await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal("original-model", client.ModelId);
    }

    [Fact]
    public async Task GetCompletionsAsync_WhenListModelsAsyncThrows_ReturnsEmpty()
    {
        // Deterministic: the injected client always throws from ListModelsAsync, regardless of
        // ambient Copilot connectivity. The handler must swallow the failure and return empty.
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(throwOnList: true));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Empty(completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithModels_ReturnsAllModels()
    {
        var models = new[]
        {
            Model("auto", "Auto"),
            Model("claude-sonnet-4.6", "Claude Sonnet 4.6"),
            Model("claude-opus-4.7", "Claude Opus 4.7"),
        };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal(3, completions.Count);
        Assert.Equal(new[] { "auto", "claude-sonnet-4.6", "claude-opus-4.7" }, completions.Select(c => c.CompletionText));
    }

    [Fact]
    public async Task GetCompletionsAsync_CompletionTextAndLabelMatchModelId()
    {
        var models = new[] { Model("gpt-5", "GPT-5") };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Equal("gpt-5", completion.CompletionText);
        Assert.Equal("gpt-5", completion.Label);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithPartialArgument_FiltersByPrefixCaseInsensitively()
    {
        var models = new[]
        {
            Model("auto", "Auto"),
            Model("claude-sonnet-4.6", "Claude Sonnet 4.6"),
            Model("claude-opus-4.7", "Claude Opus 4.7"),
            Model("gpt-5", "GPT-5"),
        };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, "CLAUDE", CancellationToken.None);

        Assert.Equal(
            new[] { "claude-sonnet-4.6", "claude-opus-4.7" },
            completions.Select(c => c.CompletionText));
    }

    [Fact]
    public async Task GetCompletionsAsync_WithPartialArgument_TrimsWhitespaceBeforeMatching()
    {
        var models = new[]
        {
            Model("auto", "Auto"),
            Model("gpt-5", "GPT-5"),
        };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, "  gpt", CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Equal("gpt-5", completion.CompletionText);
    }

    [Fact]
    public async Task GetCompletionsAsync_WithEmptyModelList_ReturnsEmpty()
    {
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(Array.Empty<ModelInfo>()));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Empty(completions);
    }

    [Fact]
    public async Task GetCompletionsAsync_Description_ContainsModelName()
    {
        var models = new[] { Model("gpt-5", "GPT-5 Turbo") };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Contains("GPT-5 Turbo", completion.Description);
    }

    [Fact]
    public async Task GetCompletionsAsync_Description_ContainsMultiplier_WhenNotOne()
    {
        var models = new[] { Model("premium", "Premium Model", multiplier: 2.5) };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Contains("Premium Model", completion.Description);
        Assert.Contains("x2.50 billing", completion.Description);
    }

    [Fact]
    public async Task GetCompletionsAsync_Description_OmitsMultiplier_WhenExactlyOne()
    {
        var models = new[] { Model("standard", "Standard Model", multiplier: 1.0) };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Equal("Standard Model", completion.Description);
        Assert.DoesNotContain("billing", completion.Description);
    }

    [Fact]
    public async Task GetCompletionsAsync_Description_OmitsMultiplier_WhenBillingNull()
    {
        var models = new[] { Model("free", "Free Model") };
        var handler = new CopilotSdkModelSlashCommandHandler(new FakeModelClient(models));
        var context = await CreateContextAsync();

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        var completion = Assert.Single(completions);
        Assert.Equal("Free Model", completion.Description);
        Assert.DoesNotContain("billing", completion.Description);
    }

    [Fact]
    public void RealCopilotSdkChatClient_ImplementsModelSeam()
    {
        // Guards the production seam: the real client must satisfy the interface the handler depends on.
        using var client = CreateRealClient("gpt-5");

        Assert.IsAssignableFrom<IModelSlashCommandClient>(client);
        Assert.Equal("gpt-5", ((IModelSlashCommandClient)client).ModelId);
    }
}

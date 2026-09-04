using Phantom.Workspaces.Llm;
using AgentSchema;
using Xunit;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for example agent definitions embedded as resources.
/// </summary>
public class ExampleAgentDefinitionsTests
{
    private static string GetEmbeddedResourceContent(string resourceName)
    {
        var assembly = typeof(ExampleAgentDefinitionsTests).Assembly;
        var fullResourceName = $"Phantom.Workspaces.Llm.Core.Tests.{resourceName}";
        
        using (var stream = assembly.GetManifestResourceStream(fullResourceName))
        {
            if (stream == null)
                throw new InvalidOperationException($"Resource not found: {fullResourceName}");
            
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }

    [Fact]
    public void LoadQwenLocalChatYaml_ValidatesSuccessfully()
    {
        var yaml = GetEmbeddedResourceContent("qwen-local-chat.yaml");
        var agent = AgentDefinitionLoader.LoadAgentFromYaml(yaml);
        
        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);
        
        var promptAgent = agent as PromptAgent;
        Assert.NotNull(promptAgent);
        Assert.Equal("qwen-local-chat", promptAgent.Name);
        Assert.Equal("qwen3.6", promptAgent.Model?.Id);
        Assert.Equal("ollama", promptAgent.Model?.Provider);
    }

    [Fact]
    public void LoadQwenLocalChatJson_ValidatesSuccessfully()
    {
        var json = GetEmbeddedResourceContent("qwen-local-chat.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);
        
        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);
        
        var promptAgent = agent as PromptAgent;
        Assert.NotNull(promptAgent);
        Assert.Equal("qwen-local-chat", promptAgent.Name);
        Assert.Equal("qwen3.6", promptAgent.Model?.Id);
        Assert.Equal("ollama", promptAgent.Model?.Provider);
    }

    [Fact]
    public void QwenLocalChatAgent_HasValidInstructions()
    {
        var yaml = GetEmbeddedResourceContent("qwen-local-chat.yaml");
        var agent = AgentDefinitionLoader.LoadAgentFromYaml(yaml);
        var promptAgent = agent as PromptAgent;
        
        Assert.NotNull(promptAgent?.Instructions);
        Assert.NotEmpty(promptAgent.Instructions);
        Assert.Contains("Qwen 3.6", promptAgent.Instructions);
    }

    [Fact]
    public void QwenLocalChatAgent_HasCorrectModelOptions()
    {
        var yaml = GetEmbeddedResourceContent("qwen-local-chat.yaml");
        var agent = AgentDefinitionLoader.LoadAgentFromYaml(yaml);
        var promptAgent = agent as PromptAgent;
        
        Assert.NotNull(promptAgent?.Model?.Options);
        Assert.NotNull(promptAgent.Model.Options.Temperature);
        Assert.NotNull(promptAgent.Model.Options.TopP);
        Assert.Equal(2048, promptAgent.Model.Options.MaxOutputTokens);
        Assert.Equal("15m", promptAgent.Model.Options.AdditionalProperties?["keep_alive"]?.ToString());
    }

    [Fact]
    public void LoadQwenLocalGithubMcp_ValidatesSuccessfully()
    {
        var json = GetEmbeddedResourceContent("qwen-local-github-mcp.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.Equal("qwen-local-github-mcp", promptAgent.Name);
        Assert.Equal("qwen3.6", promptAgent.Model?.Id);
        Assert.Equal("ollama", promptAgent.Model?.Provider);
    }

    [Fact]
    public void LoadQwenLocalGithubMcp_HasMcpTool()
    {
        var json = GetEmbeddedResourceContent("qwen-local-github-mcp.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.NotNull(promptAgent.Tools);

        var mcpTool = Assert.Single(promptAgent.Tools!.OfType<McpTool>());
        Assert.Equal("github", mcpTool.Name);
        Assert.Equal("github", mcpTool.ServerName);
        Assert.NotNull(mcpTool.Connection);
    }

    [Fact]
    public void LoadQwenLocalGithubMcp_HasWebRequestTool()
    {
        var json = GetEmbeddedResourceContent("qwen-local-github-mcp.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.NotNull(promptAgent.Tools);

        var webRequestTool = Assert.Single(promptAgent.Tools!.OfType<CustomTool>(), t => t.Kind == "web_request");
        Assert.Equal("web_request", webRequestTool.Kind);
    }

    [Fact]
    public void LoadQwenLocalChatThinking_UsesKeepAlive()
    {
        var json = GetEmbeddedResourceContent("qwen-local-chat-thinking.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);
        var promptAgent = Assert.IsType<PromptAgent>(agent);

        Assert.Equal("15m", promptAgent.Model?.Options?.AdditionalProperties?["keep_alive"]?.ToString());
    }

    [Fact]
    public void LoadQwenLocalChatWithMongoDb_UsesKeepAlive()
    {
        var json = GetEmbeddedResourceContent("qwen-local-chat-with-mongodb.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);
        var promptAgent = Assert.IsType<PromptAgent>(agent);

        Assert.Equal("15m", promptAgent.Model?.Options?.AdditionalProperties?["keep_alive"]?.ToString());
    }

    [Fact]
    public void LoadGithubModelsChat_ValidatesSuccessfully()
    {
        var json = GetEmbeddedResourceContent("github-models-chat.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.Equal("github-models-chat", promptAgent.Name);
        Assert.Equal("gpt-4.1-mini", promptAgent.Model?.Id);
        Assert.Equal("github-models", promptAgent.Model?.Provider);
    }

    [Fact]
    public void LoadGithubModelsChat_HasMcpAndWebRequestTools()
    {
        var json = GetEmbeddedResourceContent("github-models-chat.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.NotNull(promptAgent.Tools);

        var mcpTool = Assert.Single(promptAgent.Tools!.OfType<McpTool>());
        Assert.Equal("github", mcpTool.Name);

        var webRequestTool = Assert.Single(promptAgent.Tools!.OfType<CustomTool>(), t => t.Kind == "web_request");
        Assert.Equal("web_request", webRequestTool.Kind);
    }

    [Fact]
    public void LoadGithubCopilotChat_ValidatesSuccessfully()
    {
        var json = GetEmbeddedResourceContent("github-copilot-chat.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.Equal("github-copilot-chat", promptAgent.Name);
        Assert.Equal("gpt-4.1-mini", promptAgent.Model?.Id);
        Assert.Equal("github-copilot", promptAgent.Model?.Provider);
    }

    [Fact]
    public void LoadGithubCopilotRemoteChat_ValidatesSuccessfully()
    {
        var json = GetEmbeddedResourceContent("github-copilot-remote-chat.json");
        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);

        Assert.NotNull(agent);
        Assert.Equal("prompt", agent.Kind);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        Assert.Equal("github-copilot-remote-chat", promptAgent.Name);
        Assert.Equal("github-copilot", promptAgent.Model?.Provider);

        // Parameter placeholders live in model.options.additionalProperties because AgentDefinition
        // has no top-level parameters block; the declared parameters (working-directory, trust-profile)
        // live in the wrapping agent-manifest (see agent-configuration.md remote-hosting example).
        var additional = promptAgent.Model?.Options?.AdditionalProperties;
        Assert.NotNull(additional);
        Assert.Equal("${working-directory}", additional!["working-directory"]?.ToString());
        Assert.Equal("${trust-profile}", additional["trust-profile"]?.ToString());

        // Parameter declarations for the remote-hosting topology are documented in the metadata block
        // so a human or LLM reading the example can copy them onto the wrapping agent-manifest.
        using var document = JsonDocument.Parse(json);
        var parametersDoc = document.RootElement
            .GetProperty("metadata")
            .GetProperty("parameters")
            .GetProperty("properties");
        var declared = parametersDoc.EnumerateArray()
            .Select(p => (Name: p.GetProperty("name").GetString(), Required: p.GetProperty("required").GetBoolean()))
            .ToList();
        Assert.Contains(declared, p => p.Name == "working-directory" && p.Required);
        Assert.Contains(declared, p => p.Name == "trust-profile" && p.Required);

        // Split executor topology: at least one tool must classify as GuiLocal (workspace-gui /
        // workspace-entity) and at least one must classify as AgentExecutor (filesystem, mcp, function...).
        Assert.NotNull(promptAgent.Tools);
        var kinds = promptAgent.Tools!.Select(t => t.Kind).ToList();
        Assert.Contains(kinds, k => ExecutorTargetResolver.ForKind(k) == ExecutorTarget.GuiLocal);
        Assert.Contains(kinds, k => ExecutorTargetResolver.ForKind(k) == ExecutorTarget.AgentExecutor);
        Assert.Contains("workspace-gui", kinds);
    }

    [Fact]
    public void LoadAgentFromJson_InvalidAgainstSupportedSchema_Throws()
    {
        const string invalidJson = """
        {
          "kind": "workflow",
          "name": "unsupported",
          "model": {
            "id": "qwen-3.6"
          }
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => AgentDefinitionLoader.LoadAgentFromJson(invalidJson));
        Assert.Contains("does not match supported AgentDefinition schema", ex.Message);
    }
}

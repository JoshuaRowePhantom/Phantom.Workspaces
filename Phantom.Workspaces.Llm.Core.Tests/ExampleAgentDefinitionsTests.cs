using Phantom.Workspaces.Llm;
using AgentSchema;
using Xunit;
using System.Reflection;
using System.Linq;

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

        var webRequestTool = Assert.Single(promptAgent.Tools!.OfType<CustomTool>().Where(t => t.Kind == "web_request"));
        Assert.Equal("web_request", webRequestTool.Kind);
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
        Assert.Equal("github", promptAgent.Model?.Provider);
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

        var webRequestTool = Assert.Single(promptAgent.Tools.OfType<CustomTool>().Where(t => t.Kind == "web_request"));
        Assert.Equal("web_request", webRequestTool.Kind);
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

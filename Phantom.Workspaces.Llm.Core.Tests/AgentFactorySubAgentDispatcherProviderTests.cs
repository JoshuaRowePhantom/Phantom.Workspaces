using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactorySubAgentDispatcherProviderTests
{
    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsSubAgentDispatcherProvider()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "sub-agent-dispatcher",
            "provider": "sub-agent-dispatcher"
          }
        }
        """);

        var result = AgentDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateChatClient_SubAgentDispatcherProvider_RoutesToDispatcherBranch()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "dispatcher-agent",
              "model": {
                "id": "sub-agent-dispatcher",
                "provider": "sub-agent-dispatcher"
              }
            }
            """);

        // The dispatcher branch is recognised but not yet wired up: it throws a distinct
        // NotSupportedException rather than the InvalidOperationException used for unknown
        // providers. Reaching this exception proves the discriminator routed to the branch.
        var exception = Assert.Throws<NotSupportedException>(() => AgentFactory.CreateChatClient(agent));
        Assert.Contains("sub-agent-dispatcher", exception.Message);
    }

    [Fact]
    public void CreateChatClient_UnknownProvider_ThrowsUnknownProviderError()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "bogus-agent",
              "model": {
                "id": "some-model",
                "provider": "ollama"
              }
            }
            """);

        // Mutate the provider to an unsupported value after schema validation to exercise the
        // default switch arm, contrasting with the recognised dispatcher branch.
        var promptAgent = Assert.IsType<AgentSchema.PromptAgent>(agent);
        promptAgent.Model!.Provider = "totally-bogus-provider";

        var exception = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(agent));
        Assert.Contains("Unknown or unsupported provider", exception.Message);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

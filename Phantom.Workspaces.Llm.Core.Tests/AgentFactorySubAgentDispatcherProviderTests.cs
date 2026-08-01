using System.Text.Json;
using AgentSchema;
using Json.Schema;
using Phantom.Workspaces.Llm;

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

        // The dispatcher branch is now fully wired: constructing without SubAgentDispatcherDependencies
        // throws a distinct InvalidOperationException naming the required dependencies (proving the
        // discriminator routed to the dispatcher branch), rather than the "Unknown or unsupported
        // provider" error used for unrecognised providers.
        var exception = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(agent));
        Assert.Contains("sub-agent-dispatcher", exception.Message);
        Assert.Contains("SubAgentDispatcherDependencies", exception.Message);
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

    // Issue #1186: A restored hosted Copilot sub-agent stub can carry a persisted
    // AgentDefinition whose PromptAgent.Model is null (the child was hosted by the
    // Copilot CLI and never had its own model, and its persisted definition round-tripped
    // as "empty"). The pre-#1186 code dereferenced Model unconditionally and threw
    // "Agent definition does not specify a model." from the same critical section that
    // the startup splash awaited, hanging the app indefinitely. The fast-path for
    // hosted sub-agents must be reachable BEFORE the null-model guard.
    [Fact]
    public async Task CreateChatClientAsync_HostedCopilotSubAgentDefinition_WithNullModel_ReturnsSubAgentClient()
    {
        // A PromptAgent with a null Model — the exact shape produced by restoring an
        // empty persisted sub-agent AgentDefinition. Achieved by loading a valid agent
        // and then clearing the Model, so the type is unquestionably PromptAgent.
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "restored-empty-child",
              "model": {
                "id": "placeholder",
                "provider": "github-copilot-subagent"
              }
            }
            """);
        var promptAgent = Assert.IsType<PromptAgent>(agent);
        promptAgent.Model = null!;

        var result = await AgentFactory.CreateChatClientAsync(promptAgent, services: null);

        // The Copilot sub-agent fast-path must have been chosen — not the null-model throw.
        Assert.NotNull(result.ChatClient);
        Assert.IsType<CopilotSubAgentChatClient>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_HostedCopilotSubAgentDefinition_WithNullModel_ReturnsSubAgentClient()
    {
        // Sibling of the async variant above: the sync entrypoint (used by unit tests
        // and the non-async construction path) must exhibit the same #1186-resilient
        // behaviour so no code path can regress the model-null hang.
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "restored-empty-child-sync",
              "model": {
                "id": "placeholder",
                "provider": "github-copilot-subagent"
              }
            }
            """);
        var promptAgent = Assert.IsType<PromptAgent>(agent);
        promptAgent.Model = null!;

        var result = AgentFactory.CreateChatClient(promptAgent);

        Assert.IsType<CopilotSubAgentChatClient>(result.ChatClient);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

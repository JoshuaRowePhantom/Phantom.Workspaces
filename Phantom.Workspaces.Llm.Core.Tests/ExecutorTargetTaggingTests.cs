using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ExecutorTargetTaggingTests
{
    private const string PromptAgentWithToolsJson =
        """
        {
          "kind": "prompt",
          "name": "tagging-agent",
          "model": { "id": "gpt-test", "provider": "openai" },
          "tools": [
            { "name": "github", "kind": "mcp", "connection": { "kind": "anonymous", "endpoint": "https://example/mcp" }, "serverName": "github" },
            { "kind": "workspace-gui" },
            { "kind": "workspace-entity" },
            { "kind": "agent-session" },
            { "kind": "filesystem" }
          ]
        }
        """;

    [Fact]
    public void AgentFactory_ExtractToolExecutorTargets_TagsEachToolByKind()
    {
        var agent = AgentDefinition.FromJson(PromptAgentWithToolsJson);
        Assert.NotNull(agent);

        var tagged = AgentFactory.ExtractToolExecutorTargets(agent!);

        var byKind = tagged.ToDictionary(entry => entry.Tool.Kind, entry => entry.Target);
        Assert.Equal(ExecutorTarget.AgentExecutor, byKind["mcp"]);
        Assert.Equal(ExecutorTarget.GuiLocal, byKind["workspace-gui"]);
        Assert.Equal(ExecutorTarget.GuiLocal, byKind["workspace-entity"]);
        Assert.Equal(ExecutorTarget.HostingInstance, byKind["agent-session"]);
        Assert.Equal(ExecutorTarget.AgentExecutor, byKind["filesystem"]);
    }

    [Fact]
    public void AgentFactory_ExtractToolExecutorTargets_NonPromptAgent_ReturnsEmpty()
    {
        var agent = AgentDefinition.FromJson(
            """
            { "kind": "workflow", "name": "workflow-agent", "steps": [] }
            """);
        Assert.NotNull(agent);

        Assert.Empty(AgentFactory.ExtractToolExecutorTargets(agent!));
    }

    [Fact]
    public void McpToolContextProvider_DefaultTarget_IsAgentExecutor()
    {
        var tool = new McpTool { ServerName = "srv", Connection = new AnonymousConnection { Endpoint = "https://example/mcp" } };

        var provider = new McpToolContextProvider(tool, NullLoggerFactory.Instance);

        Assert.Equal(ExecutorTarget.AgentExecutor, provider.ExecutorTarget);
    }

    [Fact]
    public void McpToolContextProvider_SuppliedTarget_IsRetained()
    {
        var tool = new McpTool { ServerName = "srv", Connection = new AnonymousConnection { Endpoint = "https://example/mcp" } };

        var provider = new McpToolContextProvider(tool, NullLoggerFactory.Instance, ExecutorTarget.HostingInstance);

        Assert.Equal(ExecutorTarget.HostingInstance, provider.ExecutorTarget);
    }
}

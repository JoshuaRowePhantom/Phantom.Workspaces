using System.Collections.Generic;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the #1416 resolution funnel: <see cref="TrackingLoadContext"/> correlates each constructed
/// object with its own source dictionary, and <see cref="PhantomAgentSchema"/> upgrades every
/// <see cref="McpTool"/> to a <see cref="PhantomMcpTool"/> carrying the resolved transport, surviving
/// a <c>ToJson()</c>/<c>FromJson()</c> round-trip.
/// </summary>
public sealed class TrackingLoadContextTests
{
    private const string TwoMcpToolsDefinition = """
    {
      "kind": "prompt",
      "name": "multi-mcp",
      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
      "tools": [
        {
          "name": "alpha",
          "kind": "mcp",
          "connection": { "kind": "Anonymous", "endpoint": "https://alpha.example/mcp/" },
          "serverName": "alpha",
          "type": "sse"
        },
        {
          "name": "beta",
          "kind": "mcp",
          "connection": { "kind": "Anonymous", "endpoint": "https://beta.example/mcp/" },
          "serverName": "beta",
          "type": "auto"
        }
      ]
    }
    """;

    private static string SingleMcpToolJson(string serverName, string? type) => $$"""
    {
      "name": {{System.Text.Json.JsonSerializer.Serialize(serverName)}},
      "kind": "mcp",
      "connection": { "kind": "Anonymous", "endpoint": "https://example/mcp/" },
      "serverName": {{System.Text.Json.JsonSerializer.Serialize(serverName)}}{{(type is null ? string.Empty : $", \"type\": {System.Text.Json.JsonSerializer.Serialize(type)}")}}
    }
    """;

    [Fact]
    public void TrackingLoadContext_MultipleMcpTools_EachCorrelatedWithOwnType()
    {
        var definition = PhantomAgentSchema.AgentDefinitionFromJson(TwoMcpToolsDefinition);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.NotNull(promptAgent.Tools);
        var mcpTools = promptAgent.Tools!.OfType<McpTool>().ToList();
        Assert.Equal(2, mcpTools.Count);

        var alpha = Assert.IsType<PhantomMcpTool>(mcpTools.Single(tool => tool.ServerName == "alpha"));
        var beta = Assert.IsType<PhantomMcpTool>(mcpTools.Single(tool => tool.ServerName == "beta"));

        // Per-object correlation: each tool's transport matches its OWN 'type', with no bleed.
        Assert.Equal(McpHttpTransport.Sse, alpha.Transport);
        Assert.Equal(McpHttpTransport.Auto, beta.Transport);
    }

    [Fact]
    public void TrackingLoadContext_PolymorphicDoubleProcess_UpgradesOnce()
    {
        // Tool.Load wraps McpTool.Load, so ProcessOutput fires twice on the same instance; the guard
        // must upgrade it exactly once (not double-wrap). Reusing ONE context for two sequential loads
        // also proves the correlation stack is left balanced/empty after each load.
        var context = PhantomAgentSchema.CreateContext();

        var first = McpTool.FromJson(SingleMcpToolJson("first", "sse"), context);
        var second = McpTool.FromJson(SingleMcpToolJson("second", "auto"), context);

        var firstPhantom = Assert.IsType<PhantomMcpTool>(first);
        var secondPhantom = Assert.IsType<PhantomMcpTool>(second);

        Assert.Equal(McpHttpTransport.Sse, firstPhantom.Transport);
        Assert.Equal(McpHttpTransport.Auto, secondPhantom.Transport);

        // Upgraded once: the connection is the original (not re-wrapped) and the base fields survive.
        Assert.IsType<AnonymousConnection>(firstPhantom.Connection);
        Assert.Equal("first", firstPhantom.ServerName);
    }

    [Fact]
    public void PhantomMcpTool_SaveThenFromJsonWithContext_PreservesType()
    {
        var original = new PhantomMcpTool
        {
            Name = "bluebird",
            Kind = "mcp",
            ServerName = "bluebird",
            Connection = new AnonymousConnection { Endpoint = "https://mcp.bluebird-ai.net/" },
            Transport = McpHttpTransport.Sse,
        };

        // Save() re-emits 'type'; reloading through the Phantom funnel restores the transport rather
        // than silently reverting to Streamable/AutoDetect.
        var json = original.ToJson();
        var reloaded = PhantomAgentSchema.McpToolFromJson(json);

        var phantom = Assert.IsType<PhantomMcpTool>(reloaded);
        Assert.Equal(McpHttpTransport.Sse, phantom.Transport);
    }

    [Fact]
    public void PhantomAgentSchema_EntraPinnedConnection_UpgradesToPhantomOAuthConnectionWithAuthority()
    {
        // #1420: an mcp-server whose connection is authenticationMode 'entra-pinned' with an authority
        // loads as a PhantomOAuthConnection carrying the (otherwise dropped) authority — proving the
        // #1416 tracking context carries the field for connections too.
        const string json = """
        {
          "name": "entra",
          "kind": "mcp",
          "connection": {
            "kind": "oauth",
            "endpoint": "https://mcp.entra.test/",
            "authenticationMode": "entra-pinned",
            "authority": "https://login.microsoftonline.com/contoso/v2.0",
            "scopes": ["api://example/.default"]
          },
          "serverName": "entra"
        }
        """;

        var tool = Assert.IsType<PhantomMcpTool>(PhantomAgentSchema.McpToolFromJson(json));
        var connection = Assert.IsType<PhantomOAuthConnection>(tool.Connection);
        Assert.Equal("https://login.microsoftonline.com/contoso/v2.0", connection.Authority);
        Assert.Equal("entra-pinned", connection.AuthenticationMode);
    }

    [Fact]
    public void PhantomAgentSchema_SystemOAuthConnection_StaysPlainOAuthConnection()
    {
        // #1420: only entra-pinned connections are upgraded; a default (system) OAuth connection is
        // left a plain OAuthConnection so it keeps using the SDK's resource-bound provider.
        const string json = """
        {
          "name": "generic",
          "kind": "mcp",
          "connection": {
            "kind": "oauth",
            "endpoint": "https://mcp.generic.test/",
            "authority": "https://login.microsoftonline.com/contoso/v2.0"
          },
          "serverName": "generic"
        }
        """;

        var tool = Assert.IsType<PhantomMcpTool>(PhantomAgentSchema.McpToolFromJson(json));
        var connection = Assert.IsType<OAuthConnection>(tool.Connection);
        Assert.IsNotType<PhantomOAuthConnection>(connection);
    }

    [Fact]
    public void PhantomOAuthConnection_SaveThenReload_PreservesAuthorityAndMode()
    {
        // #1420: Save() re-emits 'authority'; reloading through the Phantom funnel restores both it and
        // the authenticationMode rather than dropping authority.
        var original = new PhantomMcpTool
        {
            Name = "entra",
            Kind = "mcp",
            ServerName = "entra",
            Connection = new PhantomOAuthConnection
            {
                Kind = "oauth",
                Endpoint = "https://mcp.entra.test/",
                AuthenticationMode = "entra-pinned",
                Authority = "https://login.microsoftonline.com/contoso/v2.0",
                Scopes = new List<string> { "api://example/.default" },
            },
        };

        var json = original.ToJson();
        var reloaded = PhantomAgentSchema.McpToolFromJson(json);

        var tool = Assert.IsType<PhantomMcpTool>(reloaded);
        var connection = Assert.IsType<PhantomOAuthConnection>(tool.Connection);
        Assert.Equal("https://login.microsoftonline.com/contoso/v2.0", connection.Authority);
        Assert.Equal("entra-pinned", connection.AuthenticationMode);
    }
}

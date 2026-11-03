using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSchema;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

internal static class GitHubMcpDefaultFixture
{
    public static AgentDefinition CreateDefinition()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(
            name => name.EndsWith(".defaults.mcp_servers.github-mcp-server.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var tool = JsonNode.Parse(document.RootElement.GetProperty("mcp-server").GetRawText())!.AsObject();
        tool["kind"] = "mcp";
        tool["name"] = "github";
        return AgentDefinition.FromJson(new JsonObject
        {
            ["kind"] = "prompt",
            ["name"] = "default-github-test",
            ["tools"] = new JsonArray(tool),
        }.ToJsonString())!;
    }
}

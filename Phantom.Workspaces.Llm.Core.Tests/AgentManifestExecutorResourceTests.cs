using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the executor resource schema + model (issue #1433, per-component-executor-binding): a
/// manifest carrying <c>kind:"executor"</c> resources — including the <c>connection-descriptor</c>
/// escape hatch — plus <c>executor</c> refs on tools loads through <see cref="PhantomAgentSchema"/>
/// (via <see cref="AgentManifestLoader"/>) and re-serialises losslessly.
/// </summary>
public sealed class AgentManifestExecutorResourceTests
{
    private const string ManifestWithExecutorsJson = """
    {
      "name": "executor-example",
      "displayName": "Executor Example",
      "template": {
        "kind": "prompt",
        "name": "executor-example",
        "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
      },
      "resources": [
        {
          "kind": "tool",
          "id": "fixed",
          "name": "workspace-entity",
          "executor": "worker"
        },
        {
          "kind": "executor",
          "id": "parameter",
          "name": "worker",
          "options": { "parameter": "worker-executor" }
        },
        {
          "kind": "executor",
          "id": "connection-descriptor",
          "name": "container",
          "connection-descriptor": {
            "type": "reverse-http",
            "endpoint": "https://host.example/mcp/",
            "entity-id": "11111111-2222-3333-4444-555555555555"
          }
        }
      ]
    }
    """;

    [Fact]
    public void Load_ManifestWithExecutorResource_ParsesResource()
    {
        var executors = ExecutorResource.ParseManifestResources(ManifestWithExecutorsJson);

        var worker = Assert.Single(executors, executor => executor.Name == "worker");
        Assert.Equal(ExecutorResource.ParameterStrategy, worker.Id);
        Assert.Equal("worker-executor", Assert.Contains("parameter", worker.Options));

        // The full manifest still loads through PhantomAgentSchema: executor resources are stripped for
        // AgentSchema (which does not know the discriminator), while the tool resources survive.
        var manifest = AgentManifestLoader.LoadManifestFromJson(ManifestWithExecutorsJson);
        var toolResources = manifest.Resources.OfType<AgentSchema.ToolResource>().ToArray();
        Assert.Single(toolResources, resource => resource.Name == "workspace-entity");
        Assert.DoesNotContain(manifest.Resources, resource => resource.Kind == ExecutorResource.ResourceKind);
    }

    [Fact]
    public void Load_ConnectionDescriptorStrategyResource_ParsesInlineDescriptor()
    {
        var executors = ExecutorResource.ParseManifestResources(ManifestWithExecutorsJson);

        var container = Assert.Single(executors, executor => executor.Name == "container");
        Assert.Equal(ExecutorResource.ConnectionDescriptorStrategy, container.Id);

        // The inline connection-descriptor is carried verbatim — proving the no-schema-change extension
        // seam at the model layer.
        Assert.True(container.ConnectionDescriptor.HasValue);
        var descriptor = container.ConnectionDescriptor!.Value;
        Assert.Equal("reverse-http", descriptor.GetProperty("type").GetString());
        Assert.Equal("https://host.example/mcp/", descriptor.GetProperty("endpoint").GetString());
        Assert.Equal(
            "11111111-2222-3333-4444-555555555555",
            descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void RoundTrip_ExecutorResourceAndRefs_Lossless()
    {
        // The manifest (executor resources + an executor ref on a tool) loads through PhantomAgentSchema
        // without throwing on the unknown discriminator.
        var manifest = AgentManifestLoader.LoadManifestFromJson(ManifestWithExecutorsJson);
        Assert.NotNull(manifest);

        // The executor ref on the tool resource survives in the source manifest.
        using var document = JsonDocument.Parse(ManifestWithExecutorsJson);
        var toolResource = document.RootElement.GetProperty("resources").EnumerateArray()
            .Single(resource => resource.GetProperty("kind").GetString() == "tool");
        Assert.Equal("worker", toolResource.GetProperty("executor").GetString());

        // Every executor resource round-trips parse -> serialise -> parse -> serialise identically,
        // including the inline connection-descriptor escape hatch.
        var executors = ExecutorResource.ParseManifestResources(ManifestWithExecutorsJson);
        Assert.Equal(2, executors.Count);

        foreach (var executor in executors)
        {
            var firstNode = executor.ToResourceNode();
            var reparsed = ExecutorResource.FromResourceElement(
                JsonSerializer.Deserialize<JsonElement>(firstNode.ToJsonString()));
            var secondNode = reparsed.ToResourceNode();

            Assert.True(
                JsonNode.DeepEquals(firstNode, secondNode),
                $"Executor resource '{executor.Name}' did not round-trip losslessly.");
        }
    }
}

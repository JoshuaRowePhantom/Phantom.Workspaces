using System;
using System.Collections.Generic;
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

    [Fact]
    public void Load_ExecutorParameter_Recognised()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "executor-parameter-example",
          "displayName": "Executor Parameter Example",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false },
              { "name": "worker-executor", "kind": "executor", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "executor-parameter-example",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        var properties = manifest.Parameters?.Properties;
        Assert.NotNull(properties);

        var executorParameter = Assert.Single(
            properties!,
            property => string.Equals(property.Name, "worker-executor", StringComparison.Ordinal));

        // Parameter kind is read from the manifest 'kind' field, not inferred by name.
        Assert.Equal(AgentManifestParameterKinds.Executor, executorParameter.Kind);
        Assert.True(AgentManifestParameterKinds.IsExecutor(executorParameter.Kind));

        var textParameter = Assert.Single(
            properties!,
            property => string.Equals(property.Name, "working-directory", StringComparison.Ordinal));
        Assert.False(AgentManifestParameterKinds.IsExecutor(textParameter.Kind));
    }

    [Fact]
    public void ExecutorParameterSelection_RecordedInParameterSelections()
    {
        // Both disambiguated selection shapes, recorded in the typed parameter-selections map
        // (string -> JsonElement) — NOT as a JSON-encoded string in the string->string parameter-values.
        var parameterSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["trust-worker"] = ExecutorParameterSelection.ForTrustProfile("defaults/trust-profiles/remote"),
            ["machine-worker"] = ExecutorParameterSelection.ForUserComputerProfile(
                "11111111-2222-3333-4444-555555555555"),
        };

        // The string->string parameter-values map is a separate channel, reserved for ${param} text
        // templating, and is unaffected by executor selections.
        var parameterValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["working-directory"] = "C:\\Projects\\MyApp",
        };

        // Round-trip the parameter-selections map through JSON (as it is persisted alongside
        // parameter-values on the agent-session entity).
        var serialized = new JsonObject();
        foreach (var selection in parameterSelections)
        {
            serialized[selection.Key] = JsonNode.Parse(selection.Value.GetRawText());
        }

        using var reloaded = JsonDocument.Parse(serialized.ToJsonString());
        var root = reloaded.RootElement;

        // Each recorded selection is a typed JSON OBJECT (not a string), and identifies both kind and id.
        var trust = root.GetProperty("trust-worker");
        Assert.Equal(JsonValueKind.Object, trust.ValueKind);
        Assert.True(ExecutorParameterSelection.TryGetTrustProfile(trust, out var trustProfile));
        Assert.Equal("defaults/trust-profiles/remote", trustProfile);
        Assert.False(ExecutorParameterSelection.TryGetUserComputerProfile(trust, out _));

        var machine = root.GetProperty("machine-worker");
        Assert.Equal(JsonValueKind.Object, machine.ValueKind);
        Assert.True(ExecutorParameterSelection.TryGetUserComputerProfile(machine, out var entityId));
        Assert.Equal("11111111-2222-3333-4444-555555555555", entityId);
        Assert.False(ExecutorParameterSelection.TryGetTrustProfile(machine, out _));

        // parameter-values remains a plain string->string map with no structured selection leakage.
        Assert.IsType<string>(parameterValues["working-directory"]);
        Assert.DoesNotContain("trust-worker", parameterValues.Keys);
    }
}

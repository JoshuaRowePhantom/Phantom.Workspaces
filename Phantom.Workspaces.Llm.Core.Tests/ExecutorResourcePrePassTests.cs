using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the executor PRE-PASS (issue #1436, per-component-executor-binding, M5): the
/// <c>kind:"executor"</c> resources are enumerated into <see cref="ExecutorBindings"/> as a DISTINCT pass
/// (independent of <c>IToolResourceFactory</c>, which returns <c>Tool?</c>), before any tool/model
/// construction — executors must exist first because tools/model reference them by name.
/// </summary>
public sealed class ExecutorResourcePrePassTests
{
    private const string ManifestJson = """
    {
      "name": "executor-prepass-example",
      "displayName": "Executor Pre-pass Example",
      "template": {
        "kind": "prompt",
        "name": "executor-prepass-example",
        "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
      },
      "resources": [
        { "kind": "tool", "id": "fixed", "name": "workspace-entity", "executor": "worker" },
        { "kind": "executor", "id": "local", "name": "here" },
        {
          "kind": "executor",
          "id": "connection-descriptor",
          "name": "worker",
          "connection-descriptor": { "type": "reverse-http", "endpoint": "https://host.example/mcp/" }
        }
      ]
    }
    """;

    [Fact]
    public void Build_EnumeratesExecutorResources_BeforeToolConstruction()
    {
        // The DISTINCT executor pass parses only kind:"executor" entries from the raw manifest JSON —
        // the tool resource ("workspace-entity") is NOT one of them and is never touched here.
        var executorResources = ExecutorResource.ParseManifestResources(ManifestJson);
        Assert.Equal(2, executorResources.Count);
        Assert.DoesNotContain(executorResources, resource => resource.Name == "workspace-entity");

        // Building the bindings runs the resolver over executor resources only — no IToolResourceFactory
        // is involved (the pre-pass takes no factory and constructs no tools).
        var bindings = ExecutorBindings.Build(
            executorResources,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null);

        Assert.Equal("local", bindings.Bindings["here"].GetProperty("type").GetString());
        Assert.Equal("reverse-http", bindings.Bindings["worker"].GetProperty("type").GetString());

        // The session executor defaults to local; only the two named executors are bound.
        Assert.Equal("local", bindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Equal(new[] { "here", "worker" }, bindings.Bindings.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolRegistryTests
{
    private sealed class StubScheduledTool : IWorkspaceTool
    {
        public StubScheduledTool(string toolType)
        {
            this.ToolType = toolType;
        }

        public string ToolType { get; }

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context) =>
            Task.FromResult(new WorkspaceToolExecutionResult());
    }

    [Fact]
    public void GetTool_ReturnsRegisteredTool()
    {
        var indexer = new StubScheduledTool("vector-indexer");
        var classifier = new StubScheduledTool("entity-classifier");
        var registry = new ScheduledToolRegistry([indexer, classifier]);

        Assert.Same(indexer, registry.GetTool("vector-indexer"));
        Assert.Same(classifier, registry.GetTool("entity-classifier"));
    }

    [Fact]
    public void TryGetTool_ReturnsFalseForUnknownType()
    {
        var registry = new ScheduledToolRegistry([new StubScheduledTool("vector-indexer")]);

        Assert.False(registry.TryGetTool("missing", out _));
    }

    [Fact]
    public void GetTool_ThrowsForUnknownType()
    {
        var registry = new ScheduledToolRegistry([new StubScheduledTool("vector-indexer")]);

        Assert.Throws<InvalidOperationException>(() => registry.GetTool("missing"));
    }

    [Fact]
    public void Constructor_ThrowsOnDuplicateToolType()
    {
        Assert.Throws<ArgumentException>(() => new ScheduledToolRegistry(
            [new StubScheduledTool("vector-indexer"), new StubScheduledTool("vector-indexer")]));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyToolType()
    {
        Assert.Throws<ArgumentException>(() => new ScheduledToolRegistry([new StubScheduledTool("  ")]));
    }

    [Fact]
    public void SeededDefaultTools_EveryToolType_ResolvesInRegistry()
    {
        // Regression for #1161: every JSON default under JsonEntities/defaults/tools/*.json declares a
        // tool-type that must be resolvable by a ScheduledToolRegistry built from the production
        // IWorkspaceTool set, otherwise the seeded schedule silently fails to run.
        var registry = new ScheduledToolRegistry(
        [
            new Phantom.Workspaces.Tools.VectorIndexerTool(),
            new Phantom.Workspaces.Tools.GitWorkspaceScanTool(),
            new Phantom.Workspaces.Tools.GitWorkspaceUpdateTool(),
            new Phantom.Workspaces.Tools.CopilotSessionDiscoveryTool(),
            new Phantom.Workspaces.Tools.VsCodeTunnelDiscoveryTool(),
            new Phantom.Workspaces.Tools.RunVsCodeTunnelTool(),
            new Phantom.Workspaces.Tools.GitHub.GitHubWorkItemDiscoveryTool(),
            new Phantom.Workspaces.Tools.AzureDevOps.AzureDevOpsWorkItemDiscoveryTool(),
            new Phantom.Workspaces.Tools.EntityClassifierTool(new NoopEntityClassifierAgentRunner()),
        ]);

        var assembly = typeof(Phantom.Workspaces.Data.SchemaPopulator).Assembly;
        const string prefix = "Phantom.Workspaces.Data.JsonEntities.defaults.tools.";
        var toolResources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, System.StringComparison.Ordinal)
                && name.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(toolResources);

        foreach (var resourceName in toolResources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var document = System.Text.Json.JsonDocument.Parse(stream!);
            var toolType = document.RootElement.GetProperty("tool-type").GetString();
            Assert.False(string.IsNullOrWhiteSpace(toolType),
                $"Seeded default tool entity '{resourceName}' is missing a tool-type.");

            Assert.True(
                registry.TryGetTool(toolType!, out _),
                $"Seeded default tool entity '{resourceName}' declares tool-type '{toolType}' which is not registered.");
        }
    }

    private sealed class NoopEntityClassifierAgentRunner : Phantom.Workspaces.Tools.IEntityClassifierAgentRunner
    {
        public System.Threading.Tasks.Task RunAsync(
            Phantom.Workspaces.Tools.EntityClassificationRequest request,
            System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}

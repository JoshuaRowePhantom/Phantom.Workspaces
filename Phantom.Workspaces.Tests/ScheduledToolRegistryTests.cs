using System;
using System.Collections.Generic;
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
}

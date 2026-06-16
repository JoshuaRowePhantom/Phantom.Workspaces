using System;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ToolResultBrowserViewModelTests
{
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            this.now = this.now.AddSeconds(1);
            return this.now;
        }
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    [Fact]
    public async Task RefreshAsync_BuildsHostToolRunTree_WithChildResults()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new AdvancingTimeProvider());

        var run = await writer.StartAsync(HostName, "vector-indexer");
        await writer.StartChildAsync(run, "sub-task");
        await writer.CompleteAsync(run, success: true);

        var browser = new ToolResultBrowserViewModel(dataAccessLayer);
        await browser.RefreshAsync();

        var host = Assert.Single(browser.Hosts);
        Assert.Equal("computer / this-machine", host.Label);

        var tool = Assert.Single(host.Children);
        Assert.Equal("vector-indexer", tool.Label);

        var runNode = Assert.Single(tool.Children);
        Assert.Equal("succeeded", runNode.Status);
        Assert.Equal("vector-indexer", runNode.ToolName);

        var subTask = Assert.Single(runNode.Children);
        Assert.Equal("sub-task", subTask.Label);

        var childRun = Assert.Single(subTask.Children);
        Assert.Equal("running", childRun.Status);
        Assert.Equal("sub-task", childRun.ToolName);
    }

    [Fact]
    public async Task RefreshAsync_EnumeratesMultipleHosts()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, new AdvancingTimeProvider());

        await writer.StartAsync(["computer", "alpha"], "git-workspace-scan");
        await writer.StartAsync(["computer", "beta"], "vector-indexer");

        var browser = new ToolResultBrowserViewModel(dataAccessLayer);
        await browser.RefreshAsync();

        var hostLabels = browser.Hosts.Select(host => host.Label).ToHashSet();
        Assert.Equal(2, browser.Hosts.Count);
        Assert.Contains("computer / alpha", hostLabels);
        Assert.Contains("computer / beta", hostLabels);
    }

    [Fact]
    public async Task RefreshAsync_NoResults_LeavesHostsEmpty()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var browser = new ToolResultBrowserViewModel(dataAccessLayer);

        await browser.RefreshAsync();

        Assert.Empty(browser.Hosts);
    }
}

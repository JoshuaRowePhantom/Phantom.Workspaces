using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the #1435 per-component-executor binding on <see cref="PhantomMcpTool"/>: the nullable
/// <c>Executor</c> field is copied by <see cref="PhantomMcpTool.From"/>, emitted by <c>Save()</c>, and
/// recovered by <see cref="PhantomAgentSchema"/> so a <c>ToJson()</c>/<c>FromJson()</c> round-trip
/// preserves it. AgentSchema silently drops the unknown <c>executor</c> property on load, so the
/// Phantom funnel is the only place it survives.
/// </summary>
public sealed class PhantomMcpToolExecutorTests
{
    [Fact]
    public void Save_WithExecutor_EmitsExecutorField()
    {
        var tool = new PhantomMcpTool
        {
            Name = "alpha",
            Kind = "mcp",
            ServerName = "alpha",
            Connection = new AnonymousConnection { Endpoint = "https://alpha.example/mcp/" },
            Executor = "remote-worker",
        };

        var saved = tool.Save();

        Assert.True(saved.TryGetValue("executor", out var executor));
        Assert.Equal("remote-worker", executor);
    }

    [Fact]
    public void Save_WithoutExecutor_OmitsExecutorField()
    {
        var tool = new PhantomMcpTool
        {
            Name = "alpha",
            Kind = "mcp",
            ServerName = "alpha",
            Connection = new AnonymousConnection { Endpoint = "https://alpha.example/mcp/" },
        };

        var saved = tool.Save();

        Assert.False(saved.ContainsKey("executor"));
    }

    [Fact]
    public void From_CopiesExecutor()
    {
        var source = new McpTool
        {
            Name = "alpha",
            Kind = "mcp",
            ServerName = "alpha",
            Connection = new AnonymousConnection { Endpoint = "https://alpha.example/mcp/" },
        };

        var phantom = PhantomMcpTool.From(source, McpHttpTransport.Sse, "remote-worker");

        Assert.Equal("remote-worker", phantom.Executor);
        Assert.Equal(McpHttpTransport.Sse, phantom.Transport);
    }

    [Fact]
    public void RoundTrip_ExecutorField_Preserved()
    {
        var original = new PhantomMcpTool
        {
            Name = "bluebird",
            Kind = "mcp",
            ServerName = "bluebird",
            Connection = new AnonymousConnection { Endpoint = "https://mcp.bluebird-ai.net/" },
            Transport = McpHttpTransport.Sse,
            Executor = "remote-worker",
        };

        var json = original.ToJson();
        var reloaded = PhantomAgentSchema.McpToolFromJson(json);

        var phantom = Assert.IsType<PhantomMcpTool>(reloaded);
        Assert.Equal("remote-worker", phantom.Executor);
        Assert.Equal(McpHttpTransport.Sse, phantom.Transport);
    }
}

using AgentSchema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class SubAgentDispatcherOptionsTests
{
    private static AgentDefinition CreateAgentDefinition(string name)
    {
        return AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "{{name}}",
          "model": { "id": "echo", "provider": "echo" }
        }
        """);
    }

    [Fact]
    public void RecencyThreshold_Default_Is48Hours()
    {
        var options = new SubAgentDispatcherOptions
        {
            AgentDefinitionTools = [],
        };

        Assert.Equal(TimeSpan.FromHours(48), options.RecencyThreshold);
    }

    [Fact]
    public void AmbiguityThreshold_Default_IsPointZeroFive()
    {
        var options = new SubAgentDispatcherOptions
        {
            AgentDefinitionTools = [],
        };

        Assert.Equal(0.05, options.AmbiguityThreshold);
    }

    [Fact]
    public void Thresholds_WhenProvided_OverrideDefaults()
    {
        var options = new SubAgentDispatcherOptions
        {
            AgentDefinitionTools = [],
            RecencyThreshold = TimeSpan.FromHours(12),
            AmbiguityThreshold = 0.2,
        };

        Assert.Equal(TimeSpan.FromHours(12), options.RecencyThreshold);
        Assert.Equal(0.2, options.AmbiguityThreshold);
    }

    [Fact]
    public void AgentDefinitionTools_RoundTrips()
    {
        var definition = CreateAgentDefinition("sub");
        var tool = new AgentDefinitionTool
        {
            Name = "default",
            Description = "The default sub-agent",
            Definition = definition,
        };

        var options = new SubAgentDispatcherOptions
        {
            AgentDefinitionTools = [tool],
        };

        var roundTripped = Assert.Single(options.AgentDefinitionTools);
        Assert.Same(tool, roundTripped);
        Assert.Equal("default", roundTripped.Name);
        Assert.Equal("The default sub-agent", roundTripped.Description);
        Assert.Same(definition, roundTripped.Definition);
    }

    [Fact]
    public void AgentDefinitionTool_RequiredMembers_AreRetained()
    {
        var definition = CreateAgentDefinition("helper");
        var tool = new AgentDefinitionTool
        {
            Name = "helper",
            Description = "A helper sub-agent",
            Definition = definition,
        };

        Assert.Equal("helper", tool.Name);
        Assert.Equal("A helper sub-agent", tool.Description);
        Assert.Same(definition, tool.Definition);
    }
}

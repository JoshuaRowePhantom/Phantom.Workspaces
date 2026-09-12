using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485: exact-contract tests for the property-based <see cref="Usage"/> and
/// <see cref="AgentInformation"/> value records introduced by the common chat surface.
/// </summary>
public sealed class UsageAndAgentInformationTests
{
    [Fact]
    public void Usage_DefaultInitialization_AllOptionalMetricsAreNull()
    {
        var usage = new Usage();
        Assert.Null(usage.TotalInputTokenCount);
        Assert.Null(usage.TotalOutputTokenCount);
        Assert.Null(usage.TotalCacheReadTokenCount);
        Assert.Null(usage.TotalCacheWriteTokenCount);
        Assert.Null(usage.TotalReasoningTokenCount);
        Assert.Null(usage.TotalSessionCostUsd);
    }

    [Fact]
    public void Usage_NamedInitializer_PreservesExactMetricTypes()
    {
        var usage = new Usage
        {
            TotalInputTokenCount = 100L,
            TotalOutputTokenCount = 200L,
            TotalCacheReadTokenCount = 300L,
            TotalCacheWriteTokenCount = 400L,
            TotalReasoningTokenCount = 500L,
            TotalSessionCostUsd = 0.125,
        };
        Assert.Equal(100L, usage.TotalInputTokenCount);
        Assert.Equal(200L, usage.TotalOutputTokenCount);
        Assert.Equal(300L, usage.TotalCacheReadTokenCount);
        Assert.Equal(400L, usage.TotalCacheWriteTokenCount);
        Assert.Equal(500L, usage.TotalReasoningTokenCount);
        Assert.Equal(0.125, usage.TotalSessionCostUsd);
    }

    [Fact]
    public void Usage_EqualValues_CompareEqual()
    {
        var a = new Usage { TotalInputTokenCount = 1, TotalSessionCostUsd = 0.5 };
        var b = new Usage { TotalInputTokenCount = 1, TotalSessionCostUsd = 0.5 };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Usage_RoundTrip_PreservesNullableCountsAndDoubleUsd()
    {
        var options = AIJsonUtilities.DefaultOptions;
        var usage = new Usage
        {
            TotalInputTokenCount = 42L,
            TotalOutputTokenCount = null,
            TotalSessionCostUsd = 1.75,
        };
        var json = JsonSerializer.Serialize(usage, options);
        var round = JsonSerializer.Deserialize<Usage>(json, options);
        Assert.Equal(usage, round);
    }

    [Fact]
    public void AgentInformation_RequiredInitProperties_AreMarkedRequired()
    {
        // The generator emits a RequiredMemberAttribute on every 'required' property.
        var requiredMemberNames = typeof(AgentInformation)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains(nameof(AgentInformation.AgentSessionId), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.AgentId), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.Name), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.DisplayName), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.Description), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.AcceptsUserInput), requiredMemberNames);
        Assert.Contains(nameof(AgentInformation.AgentDefinition), requiredMemberNames);
        Assert.DoesNotContain(nameof(AgentInformation.CurrentModelId), requiredMemberNames);
    }

    private static AgentDefinition MakeDefinition() =>
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    [Fact]
    public void AgentInformation_NamedInitializer_PreservesAllFields()
    {
        var def = MakeDefinition();
        var info = new AgentInformation
        {
            AgentSessionId = "S",
            AgentId = "A",
            Name = "N",
            DisplayName = "D",
            Description = "Desc",
            AcceptsUserInput = true,
            CurrentModelId = "m",
            AgentDefinition = def,
        };
        Assert.Equal("S", info.AgentSessionId);
        Assert.Equal("A", info.AgentId);
        Assert.Equal("N", info.Name);
        Assert.Equal("D", info.DisplayName);
        Assert.Equal("Desc", info.Description);
        Assert.True(info.AcceptsUserInput);
        Assert.Equal("m", info.CurrentModelId);
        Assert.Same(def, info.AgentDefinition);
    }

    [Fact]
    public void AgentInformation_EqualValues_CompareEqual()
    {
        var def = MakeDefinition();
        var a = new AgentInformation
        {
            AgentSessionId = "s",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "e",
            AcceptsUserInput = false,
            AgentDefinition = def,
        };
        var b = a;
        Assert.Equal(a, b);
    }
}

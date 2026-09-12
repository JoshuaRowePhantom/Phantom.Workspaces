using System.Reflection;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatInterfaceTests
{
    [Fact]
    public void AgentChat_ImplementsCommonChatSurface()
    {
        Assert.True(typeof(IAgentChat).IsAssignableFrom(typeof(AgentChat)));
    }

    [Fact]
    public void CommonSurface_ExposesInterfaceQueueAggregate()
    {
        var property = typeof(IAgentChat).GetProperty(nameof(IAgentChat.InputQueues));
        Assert.NotNull(property);
        Assert.Equal(typeof(IAgentInputQueues), property.PropertyType);
    }

    [Fact]
    public void RunningAgentChatLease_AgentChatProperty_ReturnsCommonSurface()
    {
        var property = typeof(RunningAgentChatLease).GetProperty(nameof(RunningAgentChatLease.AgentChat));
        Assert.NotNull(property);
        Assert.Equal(typeof(IAgentChat), property.PropertyType);
    }

    [Fact]
    public void SlashCommandContext_AgentChatProperty_ReturnsCommonSurface()
    {
        var property = typeof(SlashCommandContext).GetProperty(nameof(SlashCommandContext.AgentChat));
        Assert.NotNull(property);
        Assert.Equal(typeof(IAgentChat), property.PropertyType);
        Assert.Contains(
            property.GetCustomAttributesData(),
            attribute => attribute.AttributeType.FullName
                == "System.Runtime.CompilerServices.RequiredMemberAttribute");
    }

    [Fact]
    public void IAgentChat_DoesNotExposeSteeringApi()
    {
        Assert.DoesNotContain(
            typeof(IAgentChat).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Contains("Steer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IAgentChat_ExposesApprovedAsyncInterruptCommand()
    {
        var interrupt = Assert.Single(
            typeof(IAgentChat).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "InterruptAsync");

        Assert.Equal(typeof(Task), interrupt.ReturnType);
        var parameter = Assert.Single(interrupt.GetParameters());
        Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
        Assert.DoesNotContain(
            typeof(IAgentChat).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "Interrupt");
        Assert.Null(typeof(IAgentChat).Assembly.GetType(
            "Phantom.Workspaces.Llm.IAsyncInterruptibleAgentChat",
            throwOnError: false));
    }
}

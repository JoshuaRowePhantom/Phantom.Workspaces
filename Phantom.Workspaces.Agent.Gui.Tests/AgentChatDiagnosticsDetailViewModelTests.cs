using System.Reflection;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatDiagnosticsDetailViewModelTests
{
    [Fact]
    public async Task Constructor_CreatesStatusLine()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(), "test-agent", loggerFactory);
        using var vm = new AgentChatDiagnosticsDetailViewModel(agentViewModel);

        Assert.NotNull(vm.StatusLine);
    }

    [Fact]
    public async Task Dispose_DoesNotThrow()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(), "test-agent", loggerFactory);
        var vm = new AgentChatDiagnosticsDetailViewModel(agentViewModel);

        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    private static AgentChat CreateChat()
    {
        var requestType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest type was not found.");
        var request = Activator.CreateInstance(requestType)
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest could not be created.");
        var agentDefinitionProperty = requestType.GetProperty("AgentDefinition")
            ?? throw new InvalidOperationException("AgentDefinition property was not found.");
        var configuredStoreProperty = requestType.GetProperty("ConfiguredStore")
            ?? throw new InvalidOperationException("ConfiguredStore property was not found.");
        agentDefinitionProperty.SetValue(request, null);
        configuredStoreProperty.SetValue(request, new InMemoryAgentPersistenceStore());

        var constructor = typeof(AgentChat).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [requestType],
            modifiers: null)
            ?? throw new InvalidOperationException("AgentChat constructor was not found.");

        return (AgentChat)constructor.Invoke([request]);
    }
}

using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerAutoScrollTests
{
    // #1259: submitting a message via the input queue must NOT change AutoScrollEnabled. Submitting is
    // a legitimate operation regardless of the user's auto-scroll preference; the queue-submit path
    // must neither force it on nor let it be flipped off. This locks in that no "force true on submit"
    // hook is added on the managed side (the rejected earlier design).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueComposer_Submit_DoesNotModifyAutoScrollEnabled(bool initialAutoScrollEnabled)
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        Assert.NotNull(viewModel.InputQueue);
        viewModel.AutoScrollEnabled = initialAutoScrollEnabled;

        var composer = viewModel.InputQueue!.DefaultComposer;
        composer.InputText = "a queued message";
        composer.Submit();

        // Submit consumed the input (sanity that the path actually ran) but left auto-scroll untouched.
        Assert.Equal(string.Empty, composer.InputText);
        Assert.Equal(initialAutoScrollEnabled, viewModel.AutoScrollEnabled);
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);
}

using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerSlashCommandTests
{
    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    [AvaloniaFact]
    public async Task Submit_WithSlashCommand_AndInterceptorSet_DoesNotQueueMessage_AndCallsInterceptor()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        var intercepted = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        composer.SlashCommandInterceptorAsync = async text =>
        {
            intercepted = true;
            await Task.Yield();
            tcs.TrySetResult();
        };

        composer.InputText = "/working-directory C:\\Projects\\Foo";
        composer.Submit();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(intercepted);
        Assert.Equal(string.Empty, composer.InputText);
        Assert.Empty(chat.DefaultInputQueue.Items);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task Submit_WithNonSlashText_QueuesNormally_WithoutCallingInterceptor()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        var interceptorCalled = false;
        composer.SlashCommandInterceptorAsync = _ =>
        {
            interceptorCalled = true;
            return Task.CompletedTask;
        };

        composer.InputText = "hello world";
        composer.Submit();

        Assert.False(interceptorCalled);
        Assert.Single(chat.DefaultInputQueue.Items);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task Submit_WithNoInterceptor_AndSlashText_QueuesNormally()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        // No interceptor set.
        composer.InputText = "/working-directory C:\\Foo";
        composer.Submit();

        Assert.Single(chat.DefaultInputQueue.Items);

        inputQueue.Dispose();
    }
}

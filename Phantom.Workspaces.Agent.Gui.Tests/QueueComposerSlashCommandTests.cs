using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;
using System.Collections.Generic;

namespace Phantom.Workspaces.Agent.Gui.Tests;

[Trait("Category", "SlowLayout")]
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

    [AvaloniaFact]
    public async Task InputText_StartingWithSlash_CallsCompletionsProvider()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        var providerCalled = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new List<SlashCommandCompletion>
        {
            new SlashCommandCompletion("working-directory", "/working-directory", "Set working directory"),
        };

        composer.SlashCompletionsProviderAsync = (commandName, partialArgs, ct) =>
        {
            providerCalled = true;
            tcs.TrySetResult();
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(completions);
        };

        composer.InputText = "/working-directory";
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(providerCalled);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task InputText_NotStartingWithSlash_DismissesCompletions()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        var completions = new List<SlashCommandCompletion>
        {
            new SlashCommandCompletion("working-directory", "/working-directory", "Set working directory"),
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        composer.SlashCompletionsProviderAsync = (commandName, partialArgs, ct) =>
        {
            tcs.TrySetResult();
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(completions);
        };

        // First trigger completions.
        composer.InputText = "/working-directory";
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now type non-slash text — completions should be dismissed.
        composer.InputText = "hello";

        Assert.False(composer.Completions.IsVisible);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task InputText_WithJustSlash_PassesSentinelEmptyCommandNameToProvider()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        string? capturedCommandName = "not-set";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        composer.SlashCompletionsProviderAsync = (commandName, partialArgs, ct) =>
        {
            capturedCommandName = commandName;
            tcs.TrySetResult();
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
        };

        composer.InputText = "/";
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, capturedCommandName);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task InputText_WithPartialCommandName_PassesSentinelEmptyCommandNameToProvider()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        string? capturedCommandName = "not-set";
        string? capturedPartialArgs = "not-set";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        composer.SlashCompletionsProviderAsync = (commandName, partialArgs, ct) =>
        {
            capturedCommandName = commandName;
            capturedPartialArgs = partialArgs;
            tcs.TrySetResult();
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
        };

        composer.InputText = "/wor";
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, capturedCommandName);
        Assert.Equal("wor", capturedPartialArgs);

        inputQueue.Dispose();
    }

    [AvaloniaFact]
    public async Task InputText_WithCommandNameAndSpace_PassesCommandNameAndPartialArgsToProvider()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });

        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        string? capturedCommandName = "not-set";
        string? capturedPartialArgs = "not-set";
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        composer.SlashCompletionsProviderAsync = (commandName, partialArgs, ct) =>
        {
            capturedCommandName = commandName;
            capturedPartialArgs = partialArgs;
            tcs.TrySetResult();
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>([]);
        };

        composer.InputText = "/working-directory /some/path";
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("working-directory", capturedCommandName);
        Assert.Equal("/some/path", capturedPartialArgs);

        inputQueue.Dispose();
    }
}

using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SessionTabSlashCommandHandlerTests
{
    [Fact]
    public async Task RestartSlashCommandHandler_Execute_InvokesReplaceWithClone()
    {
        var called = false;
        var handler = new RestartSlashCommandHandler();
        var context = await CreateContextAsync(replaceWithCloneAsync: ct =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.True(called);
        Assert.Equal("Session restarted.", result.StatusMessage);
    }

    [Fact]
    public async Task RestartSlashCommandHandler_Execute_WhenDelegateNull_ReturnsError()
    {
        var handler = new RestartSlashCommandHandler();
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal("Cannot restart: session cloning is not available.", result.StatusMessage);
    }

    [Fact]
    public async Task CloneSlashCommandHandler_Execute_InvokesOpenCloneInNewTab()
    {
        var called = false;
        var handler = new CloneSlashCommandHandler();
        var context = await CreateContextAsync(openCloneInNewTabAsync: ct =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.True(called);
        Assert.Equal("Session cloned.", result.StatusMessage);
    }

    [Fact]
    public async Task CloneSlashCommandHandler_Execute_WhenDelegateNull_ReturnsError()
    {
        var handler = new CloneSlashCommandHandler();
        var context = await CreateContextAsync();

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal("Cannot clone: session cloning is not available.", result.StatusMessage);
    }

    [Fact]
    public async Task RenameSlashCommandHandler_Execute_WithName_InvokesRenameSession()
    {
        string? renamedTo = null;
        var handler = new RenameSlashCommandHandler();
        var context = await CreateContextAsync(renameSessionAsync: (name, ct) =>
        {
            renamedTo = name;
            return Task.CompletedTask;
        });

        var result = await handler.ExecuteAsync(context, "  Foo  ", CancellationToken.None);

        Assert.Equal("Foo", renamedTo);
        Assert.Equal("Session renamed to \"Foo\".", result.StatusMessage);
    }

    [Fact]
    public async Task RenameSlashCommandHandler_Execute_EmptyArguments_ReturnsError()
    {
        var handler = new RenameSlashCommandHandler();
        var context = await CreateContextAsync(renameSessionAsync: (_, _) => Task.CompletedTask);

        var result = await handler.ExecuteAsync(context, "   ", CancellationToken.None);

        Assert.Equal("Usage: /rename <new name>", result.StatusMessage);
    }

    [Fact]
    public async Task TitleSlashCommandHandler_Execute_WithName_InvokesSetTabTitle()
    {
        string? titleSetTo = null;
        var handler = new TitleSlashCommandHandler();
        var context = await CreateContextAsync(setTabTitleAsync: (title, ct) =>
        {
            titleSetTo = title;
            return Task.CompletedTask;
        });

        var result = await handler.ExecuteAsync(context, "  Foo  ", CancellationToken.None);

        Assert.Equal("Foo", titleSetTo);
        Assert.Equal("Tab title set to \"Foo\".", result.StatusMessage);
    }

    [Fact]
    public async Task TitleSlashCommandHandler_Execute_EmptyArguments_ReturnsError()
    {
        var handler = new TitleSlashCommandHandler();
        var context = await CreateContextAsync(setTabTitleAsync: (_, _) => Task.CompletedTask);

        var result = await handler.ExecuteAsync(context, "   ", CancellationToken.None);

        Assert.Equal("Usage: /title <new title>", result.StatusMessage);
    }

    private static async Task<SlashCommandContext> CreateContextAsync(
        Func<string, CancellationToken, Task>? renameSessionAsync = null,
        Func<CancellationToken, Task>? replaceWithCloneAsync = null,
        Func<CancellationToken, Task>? openCloneInNewTabAsync = null,
        Func<string, CancellationToken, Task>? setTabTitleAsync = null)
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        return new SlashCommandContext
        {
            AgentChat = chat,
            RenameSessionAsync = renameSessionAsync,
            ReplaceWithCloneAsync = replaceWithCloneAsync,
            OpenCloneInNewTabAsync = openCloneInNewTabAsync,
            SetTabTitleAsync = setTabTitleAsync,
        };
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

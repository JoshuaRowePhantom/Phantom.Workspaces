using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class AgentChatSlashCommandRegistrationTests
{
    private static AgentDefinition CreateCopilotAgent() =>
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "copilot-agent",
          "model": { "id": "gpt-5", "provider": "github-copilot", "apiType": "OpenAI" }
        }
        """);

    private static AgentDefinition CreateEchoAgent() =>
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    [Fact]
    public async Task SlashCommands_ForEchoAgent_IncludesHelp()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateEchoAgent(),
        });

        Assert.Contains(chat.SlashCommands.Commands, c => c.Name == "help");
    }

    [Fact]
    public async Task SlashCommands_ForEchoAgent_DoesNotIncludeWorkingDirectory()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateEchoAgent(),
        });

        Assert.DoesNotContain(chat.SlashCommands.Commands, c => c.Name == "working-directory");
    }

    [Fact]
    public async Task SlashCommands_HelpCommand_IsHelpSlashCommandHandler()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateEchoAgent(),
        });

        var help = Assert.Single(chat.SlashCommands.Commands, c => c.Name == "help");
        Assert.IsType<HelpSlashCommandHandler>(help);
    }

    [Fact]
    public async Task SlashCommands_ForCopilotAgent_WorkingDirectoryCommand_IsCopilotSdkSpecificHandler()
    {
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            TaskScheduler.Default);
        await using var lease = await factory.CreateAsync(
            CreateCopilotAgent(),
            new AgentSessionId(Guid.NewGuid().ToString("n")));
        var chat = lease.AgentChat;

        var handler = Assert.Single(chat.SlashCommands.Commands, c => c.Name == "working-directory");
        Assert.IsType<CopilotSdkWorkingDirectorySlashCommandHandler>(handler);
    }

    [Fact]
    public async Task SlashCommands_ForCopilotAgent_IncludesModelCommand()
    {
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            TaskScheduler.Default);
        await using var lease = await factory.CreateAsync(
            CreateCopilotAgent(),
            new AgentSessionId(Guid.NewGuid().ToString("n")));
        var chat = lease.AgentChat;

        Assert.Contains(chat.SlashCommands.Commands, c => c.Name == "model");
    }

    [Fact]
    public void AgentChat_DoesNotDirectlyReferenceConcreteAgentTypes()
    {
        // Static analysis: AgentChat.cs must not contain slash-command registrations for
        // concrete agent-type handlers. Those are now self-registered by the components.
        var agentChatSource = System.IO.File.ReadAllText(
            FindSourceFile("AgentChat.cs"));

        Assert.DoesNotContain("CopilotSdkWorkingDirectorySlashCommandHandler", agentChatSource);
        Assert.DoesNotContain("CopilotSdkModelSlashCommandHandler", agentChatSource);
        Assert.DoesNotContain("RegisterSlashCommands", agentChatSource);
    }

    private static string FindSourceFile(string fileName)
    {
        // Walk up from the test assembly output directory to the repo root,
        // then locate the source file in the Llm.Core project.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Phantom.Workspaces.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(dir!.FullName, "Phantom.Workspaces.Llm.Core", fileName);
        Assert.True(System.IO.File.Exists(path), $"Source file not found: {path}");
        return path;
    }
}

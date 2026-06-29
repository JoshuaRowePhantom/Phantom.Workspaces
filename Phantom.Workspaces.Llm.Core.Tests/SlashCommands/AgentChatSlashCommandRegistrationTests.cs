using AgentSchema;
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
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateCopilotAgent(),
        });

        var handler = Assert.Single(chat.SlashCommands.Commands, c => c.Name == "working-directory");
        Assert.IsType<CopilotSdkWorkingDirectorySlashCommandHandler>(handler);
    }
}

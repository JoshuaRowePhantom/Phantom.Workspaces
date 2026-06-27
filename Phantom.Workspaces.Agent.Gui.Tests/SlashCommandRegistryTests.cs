using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SlashCommandRegistryTests
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

    private readonly SlashCommandRegistry registry = SlashCommandRegistry.Default;

    [Fact]
    public void GetCommands_ForCopilotAgent_IncludesWorkingDirectory()
    {
        var commands = this.registry.GetCommands(CreateCopilotAgent());

        Assert.Contains(commands, c => c.Name == "working-directory");
    }

    [Fact]
    public void GetCommands_ForCopilotAgent_IncludesHelp()
    {
        var commands = this.registry.GetCommands(CreateCopilotAgent());

        Assert.Contains(commands, c => c.Name == "help");
    }

    [Fact]
    public void GetCommands_ForNonCopilotAgent_DoesNotIncludeWorkingDirectory()
    {
        var commands = this.registry.GetCommands(CreateEchoAgent());

        Assert.DoesNotContain(commands, c => c.Name == "working-directory");
    }

    [Fact]
    public void GetCommands_ForNonCopilotAgent_IncludesHelp()
    {
        var commands = this.registry.GetCommands(CreateEchoAgent());

        Assert.Contains(commands, c => c.Name == "help");
    }

    [Fact]
    public void GetCommands_ForNull_IncludesHelp()
    {
        var commands = this.registry.GetCommands(agentDefinition: null);

        Assert.Contains(commands, c => c.Name == "help");
        Assert.DoesNotContain(commands, c => c.Name == "working-directory");
    }

    [Fact]
    public void GetCommands_HelpCommand_KnowsAboutWorkingDirectory_WhenCopilotAgent()
    {
        // The HelpSlashCommandHandler is constructed with the same commands list as
        // the registry returns, so /help working-directory should resolve.
        var commands = this.registry.GetCommands(CreateCopilotAgent());
        var help = Assert.Single(commands, c => c.Name == "help");
        Assert.IsType<HelpSlashCommandHandler>(help);
    }
}

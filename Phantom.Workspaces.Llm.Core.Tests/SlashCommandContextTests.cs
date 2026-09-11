using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SlashCommandContextTests
{
    [Fact]
    public async Task AgentChatSetter_LocalOrRemote_PreservesCommonChat()
    {
        await using var local = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateDefinition() });
        await using var remote = new RemoteAgentChatProxy(local);

        Assert.Same(local, new SlashCommandContext { AgentChat = local }.AgentChat);
        Assert.Same(remote, new SlashCommandContext { AgentChat = remote }.AgentChat);
    }

    [Fact]
    public void AgentChatSetter_Null_RejectsInitialization()
    {
        Assert.Throws<ArgumentNullException>(() => new SlashCommandContext { AgentChat = null! });
    }

    private static AgentSchema.AgentDefinition CreateDefinition() =>
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "slash-context",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """);
}

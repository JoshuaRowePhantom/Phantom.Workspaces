using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class SecretStoreEndToEndTests
{
    [Fact]
    public async Task SecretStoreEndToEnd_BrokerLease_ReachesSdkWithoutPersistingMaterial()
    {
        var definition = AgentDefinition.FromJson("""
            {"kind":"prompt","name":"test","model":{"id":"gpt-5-mini","provider":"github-copilot","options":{"additionalProperties":{"ApiToken":"${SECRET:Token}"}}}}
            """)!;
        await using var broker = new ManifestAccessTestBroker { Material = "gho_test" };
        await using var materialized = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);
        var opaque = definition.ToJson();
        var result = await AgentFactory.CreateChatClientAsync(
            definition,
            new AgentServices { SecretPlaceholderResolver = materialized.Resolver },
            cancellationToken: CancellationToken.None);
        Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.DoesNotContain("gho_test", opaque);
        Assert.Equal(opaque, definition.ToJson());
        Assert.Single(broker.Requests);
        result.ChatClient.Dispose();
    }
}

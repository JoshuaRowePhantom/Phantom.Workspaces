using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class AgentManifestAgentDefinitionToolSchemaTests
{
    private static async Task<SchemaValidatingDataAccessLayer> CreatePopulatedComposerAsync()
    {
        var underlying = new InMemoryDataAccessLayer();
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(new ReferentialIntegrityDataAccessLayer(underlying));
        var populator = new SchemaPopulator(dataAccessLayer);
        Assert.Empty(await populator.Populate());
        return dataAccessLayer;
    }

    [Fact]
    public async Task AgentManifest_WithAgentTypeDiscriminator_Validates()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "agent-type": "sub-agent-dispatcher",
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task AgentManifest_WithInlineAgentDefinitionTool_Validates()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "agent-type": "sub-agent-dispatcher",
            "tools": [
              {
                "kind": "agent-definition",
                "name": "default",
                "description": "The default sub-agent",
                "definition": {
                  "kind": "prompt",
                  "name": "sub",
                  "model": { "id": "echo", "provider": "echo" }
                }
              }
            ],
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task AgentManifest_WithManifestReferenceAgentDefinitionTool_Validates()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "agent-type": "sub-agent-dispatcher",
            "tools": [
              {
                "kind": "agent-definition",
                "name": "helper",
                "description": "A referenced sub-agent",
                "manifest-reference": ["agent-manifests", "helper"]
              }
            ],
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task AgentManifest_AgentDefinitionToolMissingKind_FailsValidation()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "tools": [
              {
                "name": "default",
                "description": "Missing kind"
              }
            ],
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task AgentManifest_AgentDefinitionToolMissingName_FailsValidation()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "tools": [
              {
                "kind": "agent-definition",
                "description": "Missing name"
              }
            ],
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task AgentManifest_AgentDefinitionToolMissingDescription_FailsValidation()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(BuildManifest("""
            "tools": [
              {
                "kind": "agent-definition",
                "name": "default"
              }
            ],
            """));

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.NotEmpty(errors);
    }

    private static string BuildManifest(string extraProperties)
    {
        return $$"""
        {
          "entity-id": "7f1c2a3b-4d5e-6f70-8192-a3b4c5d6e7f8",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "dispatcher-manifest"]],
          "display-name": { "default": "Dispatcher" },
          {{extraProperties}}
          "manifest": {
            "name": "dispatcher",
            "displayName": "Dispatcher",
            "template": {
              "kind": "prompt",
              "name": "dispatcher",
              "model": { "id": "echo", "provider": "echo" }
            }
          }
        }
        """;
    }
}

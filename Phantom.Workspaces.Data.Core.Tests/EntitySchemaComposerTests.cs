using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class EntitySchemaComposerTests
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
    public async Task GetValidationErrorsAsync_ValidEntity_ReturnsNoErrors()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["note"],
              "names": [["tests", "valid-note"]],
              "display-name": { "default": "Valid" },
              "content": { "default": { "mime-type": "text/markdown", "content": { "text": "hello" } } }
            }
            """);

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task GetValidationErrorsAsync_InvalidEntity_ReturnsErrors()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        // agent-manifest entity missing the required "manifest" property.
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["agent-manifest"],
              "names": [["tests", "invalid-manifest"]],
              "display-name": { "default": "Invalid" }
            }
            """);

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.NotEmpty(errors);
    }
}

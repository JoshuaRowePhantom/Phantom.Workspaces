using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class JsonValidationViewModelTests
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
    public async Task UpdateAsync_SyntacticallyInvalidJson_ReportsParseError()
    {
        var validation = new JsonValidationViewModel();

        await validation.UpdateAsync("{ not valid json ");

        Assert.False(validation.IsValid);
        Assert.True(validation.HasError);
        Assert.NotEqual(JsonValidationViewModel.ValidStatusText, validation.StatusText);
    }

    [Fact]
    public async Task UpdateAsync_NoComposer_ValidJsonIsValid()
    {
        var validation = new JsonValidationViewModel();

        await validation.UpdateAsync("""{ "a": 1 }""");

        Assert.True(validation.IsValid);
        Assert.False(validation.HasError);
        Assert.Equal(JsonValidationViewModel.ValidStatusText, validation.StatusText);
    }

    [Fact]
    public async Task UpdateAsync_SchemaInvalidEntity_ReportsSchemaError()
    {
        var composer = await CreatePopulatedComposerAsync();
        var validation = new JsonValidationViewModel(composer);

        // agent-manifest entity missing required "manifest".
        await validation.UpdateAsync(
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "invalid"]]
            }
            """);

        Assert.False(validation.IsValid);
        Assert.True(validation.HasError);
    }

    [Fact]
    public async Task UpdateAsync_SchemaValidEntity_IsValid()
    {
        var composer = await CreatePopulatedComposerAsync();
        var validation = new JsonValidationViewModel(composer);

        await validation.UpdateAsync(
            """
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "note"],
              "names": [["tests", "valid"]],
              "display-name": { "default": "Valid" },
              "content": { "default": { "mime-type": "text/markdown", "content": { "text": "hi" } } }
            }
            """);

        Assert.True(validation.IsValid);
        Assert.False(validation.HasError);
        Assert.Equal(JsonValidationViewModel.ValidStatusText, validation.StatusText);
    }
}

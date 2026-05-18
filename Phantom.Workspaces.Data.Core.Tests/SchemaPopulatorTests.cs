using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class SchemaPopulatorTests
{
    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_IntoInMemoryStore()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var countingDataAccessLayer = new CountingDataAccessLayer(validatedDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(countingDataAccessLayer);

        var errors = await schemaPopulator.Populate();

        Assert.Equal(1, countingDataAccessLayer.UpdateCallCount);
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));
    }

    [Fact]
    public async Task Populate_CreatesExpectedNumberOfDistinctEntities()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);
        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var embeddedSchemaResources = Assembly
            .GetAssembly(typeof(SchemaPopulator))!
            .GetManifestResourceNames()
            .Where(
                resourceName => (resourceName.StartsWith("Phantom.Workspaces.Data.JsonSchemas.", StringComparison.Ordinal)
                                 || resourceName.StartsWith("Phantom.Workspaces.Data.JsonEntities.", StringComparison.Ordinal))
                                && resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(embeddedSchemaResources);
        var expectedSchemaEntityCount = embeddedSchemaResources.Length;

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var distinctEntityIds = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.EntityId)
            .Distinct()
            .Count();
        Assert.Equal(expectedSchemaEntityCount, distinctEntityIds);
    }

    [Fact]
    public async Task Populate_SetsGettingStartedContent_ToMarkdownAttachment()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);
        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var gettingStartedEntity = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(["documentation", "getting-started"])));

        Assert.True(
            gettingStartedEntity.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("default", out var defaultContent)
            && defaultContent.ValueKind == JsonValueKind.Object
            && defaultContent.TryGetProperty("mime-type", out var mimeType)
            && mimeType.ValueKind == JsonValueKind.String
            && string.Equals(mimeType.GetString(), "text/markdown", StringComparison.Ordinal)
            && defaultContent.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && string.Equals(url.GetString(), "documentation/getting-started.md", StringComparison.Ordinal),
            "getting-started content was not populated as a markdown attachment");
    }

    [Fact]
    public async Task Populate_CreatesDefaultWorkspacesProfile()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);
        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var defaultProfile = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(["defaults", "profiles", "default"])));

        Assert.True(
            defaultProfile.TryGetProperty("theme", out var theme)
            && theme.ValueKind == JsonValueKind.String
            && string.Equals(theme.GetString(), "dark", StringComparison.Ordinal)
            && defaultProfile.TryGetProperty("initial-workspace", out var initialWorkspace)
            && initialWorkspace.ValueKind == JsonValueKind.String
            && string.Equals(initialWorkspace.GetString(), "6cc39f41-2a36-4be6-ab95-3f3fd355e463", StringComparison.Ordinal)
            && defaultProfile.TryGetProperty("opened-workspaces", out var openedWorkspaces)
            && openedWorkspaces.ValueKind == JsonValueKind.Array
            && openedWorkspaces.EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["6cc39f41-2a36-4be6-ab95-3f3fd355e463"]),
            "default workspaces profile was not populated correctly");
    }

    private static IDataAccessLayer CreateValidatedDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
    {
        return new SchemaValidatingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(underlyingDataAccessLayer));
    }

    private sealed class CountingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
    {
        public CountingDataAccessLayer(
            IDataAccessLayer inner)
            : base(inner)
        {
        }

        public int UpdateCallCount { get; private set; }

        public override Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            return base.UpdateAsync(request, cancellationToken);
        }
    }
}

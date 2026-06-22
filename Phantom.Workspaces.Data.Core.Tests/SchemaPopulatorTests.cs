using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

#pragma warning disable CS0618
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
                resourceName => resourceName.StartsWith("Phantom.Workspaces.Data.JsonEntities.", StringComparison.Ordinal)
                                && resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(embeddedSchemaResources);
        var expectedEntityIds = new HashSet<EntityId>();
        foreach (var resourceName in embeddedSchemaResources)
        {
            await using var stream = Assembly.GetAssembly(typeof(SchemaPopulator))!.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var document = await JsonDocument.ParseAsync(stream!);
            Assert.True(document.RootElement.TryGetProperty("entity-id", out var entityIdElement));
            Assert.True(entityIdElement.ValueKind == JsonValueKind.String);
            expectedEntityIds.Add(new EntityId(entityIdElement.GetString()!));
        }

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var latestEntitiesById = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .GroupBy(static entity => entity.EntityId)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(entity => entity.ModifiedTime.DateTime).First());
        Assert.True(expectedEntityIds.IsSubsetOf(latestEntitiesById.Keys));

        var extraEntities = latestEntitiesById
            .Where(pair => !expectedEntityIds.Contains(pair.Key))
            .Select(static pair => pair.Value)
            .ToArray();
        Assert.All(extraEntities, static entity => Assert.True(IsFolderEntity(entity.Data)));
    }

    [Fact]
    public async Task Populate_SeedsAgentManifestEntityTypeAndSchema()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);

        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var seededNames = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Where(static data => data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            .SelectMany(static data => data.GetProperty("names").EnumerateArray())
            .Select(static name => name.TryReadEntityName())
            .Where(static name => name is not null)
            .Select(static name => name!.Value.Components)
            .ToArray();

        string[][] expected =
        [
            ["entity-types", "agent-manifest"],
            ["entity-types", "agent-manifest-json-schema"],
        ];

        foreach (var expectedName in expected)
        {
            Assert.Contains(
                seededNames,
                components => components.SequenceEqual(expectedName, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Populate_ThenWritingValidAgentManifestEntity_Succeeds()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);
        await schemaPopulator.Populate();

        var validManifestEntity = JsonDocument.Parse(
            """
            {
              "entity-id": "11111111-2222-3333-4444-555555555555",
              "entity-types": [ "agent-manifest" ],
              "names": [ [ "test", "agent-manifests", "example" ] ],
              "display-name": { "default": "Example Manifest" },
              "manifest": {
                "kind": "prompt",
                "name": "example",
                "model": { "id": "gpt-4.1-mini", "provider": "github-models" },
                "toolResources": [
                  { "type": "mcp-server-entity", "name": "github" }
                ]
              }
            }
            """).RootElement.Clone();

        var updateResult = await validatedDataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Write valid agent-manifest entity." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId("11111111-2222-3333-4444-555555555555"),
                        Data = validManifestEntity,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        var failures = updateResult.EntityResults.SelectMany(static result => result.Errors).ToArray();
        Assert.True(
            failures.Length == 0,
            string.Join(Environment.NewLine, failures.Select(error => error.Message)));
    }

    [Fact]
    public async Task Populate_ThenWritingInvalidAgentManifestEntity_Fails()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);
        await schemaPopulator.Populate();

        var invalidManifestEntity = JsonDocument.Parse(
            """
            {
              "entity-id": "66666666-7777-8888-9999-000000000000",
              "entity-types": [ "agent-manifest" ],
              "names": [ [ "test", "agent-manifests", "invalid" ] ],
              "display-name": { "default": "Invalid Manifest" },
              "manifest": {
                "kind": "prompt",
                "name": "invalid"
              }
            }
            """).RootElement.Clone();

        var updateResult = await validatedDataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Write invalid agent-manifest entity." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId("66666666-7777-8888-9999-000000000000"),
                        Data = invalidManifestEntity,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.Contains(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task Populate_SeedsDefaultTrustProfiles()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);

        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var seededNames = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Where(static data => data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            .SelectMany(static data => data.GetProperty("names").EnumerateArray())
            .Select(static name => name.TryReadEntityName())
            .Where(static name => name is not null)
            .Select(static name => name!.Value.Components)
            .ToArray();

        string[][] expectedTrustProfiles =
        [
            ["trust-profiles", "current-machine"],
            ["trust-profiles", "all-machines"],
            ["trust-profiles", "all-tools"],
            ["trust-profiles", "no-tool"],
            ["trust-profiles", "workspace-read-only"],
        ];

        foreach (var expected in expectedTrustProfiles)
        {
            Assert.Contains(
                seededNames,
                components => components.SequenceEqual(expected, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Populate_SeedsDefaultAgentDefinitionsScheduleAndProfile()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = CreateValidatedDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);

        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var seededNames = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Where(static data => data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            .SelectMany(static data => data.GetProperty("names").EnumerateArray())
            .Select(static name => name.TryReadEntityName())
            .Where(static name => name is not null)
            .Select(static name => name!.Value.Components)
            .ToArray();

        string[][] expectedDefaults =
        [
            ["defaults", "agent-definitions", "workspaces"],
            ["defaults", "agent-definitions", "github-copilot"],
            ["defaults", "agent-definitions", "github-models"],
            ["defaults", "profiles", "default"],
            ["schedule", "every-day-at-09"],
        ];

        foreach (var expected in expectedDefaults)
        {
            Assert.Contains(
                seededNames,
                components => components.SequenceEqual(expected, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Populate_EntityTypeSchema_IsEntityTypeAndRequiresSchema()
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
        var entityTypeSchema = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                {
                    var parsedName = name.TryReadEntityName();
                    return parsedName is not null
                        && parsedName.Value.Components.SequenceEqual(
                            ["entity-types", "entity-type"],
                            StringComparer.Ordinal);
                }));

        Assert.True(
            entityTypeSchema.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array
            && entityTypes.EnumerateArray().Any(type =>
                type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "entity-type", StringComparison.Ordinal))
            && !entityTypes.EnumerateArray().Any(type =>
                type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "json-schema", StringComparison.Ordinal))
            && entityTypeSchema.TryGetProperty("schema", out var schema)
            && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("allOf", out var allOf)
            && allOf.ValueKind == JsonValueKind.Array
            && allOf.EnumerateArray().Any(definition =>
                definition.ValueKind == JsonValueKind.Object
                && definition.TryGetProperty("$ref", out var reference)
                && reference.ValueKind == JsonValueKind.String
                && string.Equals(
                    reference.GetString(),
                    "https://schemas.workspaces.phantom.to/workspaces/data/core/json-schema.json",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Populate_MaterializesGettingStartedMarkdownUrl_ToInlineContent()
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
            && !defaultContent.TryGetProperty("url", out _)
            && defaultContent.TryGetProperty("content", out var inlineContent)
            && inlineContent.ValueKind == JsonValueKind.Object
            && inlineContent.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && text.GetString()!.Contains("# Getting Started", StringComparison.Ordinal),
            "getting-started markdown attachment was not materialized into inline content");
    }

    [Fact]
    public async Task Populate_MaterializesSchemaDocumentationMarkdownUrl_ToInlineContent()
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
        var coreSchemaEntity = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(
                        ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json"])));

        Assert.True(
            coreSchemaEntity.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("default", out var defaultContent)
            && defaultContent.ValueKind == JsonValueKind.Object
            && !defaultContent.TryGetProperty("url", out _)
            && defaultContent.TryGetProperty("content", out var inlineContent)
            && inlineContent.ValueKind == JsonValueKind.Object
            && inlineContent.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && text.GetString()!.Contains("# Core schema", StringComparison.Ordinal),
            "schema documentation markdown attachment was not materialized into inline content");
    }

    [Fact]
    public async Task Populate_MaterializesSchemaReference_ToInlineSchemaObject()
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
        var coreSchemaEntity = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(
                        ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json"])));

        Assert.True(
            coreSchemaEntity.TryGetProperty("schema", out var schema)
            && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$id", out var schemaId)
            && schemaId.ValueKind == JsonValueKind.String
            && string.Equals(
                schemaId.GetString(),
                "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json",
                StringComparison.Ordinal)
            && !schema.TryGetProperty("$ref", out _));
    }

    [Fact]
    public async Task Populate_MaterializesAllEmbeddedMarkdownAttachments_ToInlineContent()
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
        var entityDocuments = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .ToArray();
        var knownEmbeddedMarkdownUrls = typeof(SchemaPopulator)
            .Assembly
            .GetManifestResourceNames()
            .Where(static resourceName =>
                resourceName.StartsWith("Phantom.Workspaces.Data.JsonEntities.", StringComparison.Ordinal)
                && resourceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(static resourceName =>
            {
                const string prefix = "Phantom.Workspaces.Data.JsonEntities.";
                var relativeName = resourceName[prefix.Length..];
                var relativeWithoutExtension = relativeName[..^3];
                return $"/JsonEntities/{relativeWithoutExtension.Replace('.', '/')}.md";
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingInlineMarkdownPaths = new List<string>();
        foreach (var entityDocument in entityDocuments)
        {
            CollectMissingInlineMarkdownPaths(entityDocument, "$", knownEmbeddedMarkdownUrls, missingInlineMarkdownPaths);
        }

        Assert.True(
            missingInlineMarkdownPaths.Count == 0,
            $"Markdown attachments missing inline content at: {string.Join(", ", missingInlineMarkdownPaths)}");
    }

    [Fact]
    public async Task Populate_RemovesEmbeddedMarkdownUrl_WhenInlineContentIsMaterialized()
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
        var azureDevOpsProjectSchemaEntity = exportResult.ChangeBatches
            .SelectMany(static changeBatch => changeBatch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .First(entity =>
                entity.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(
                        ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/azure-devops-project.json"])));

        Assert.True(
            azureDevOpsProjectSchemaEntity.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("default", out var defaultContent)
            && defaultContent.ValueKind == JsonValueKind.Object
            && defaultContent.TryGetProperty("mime-type", out var mimeType)
            && mimeType.ValueKind == JsonValueKind.String
            && string.Equals(mimeType.GetString(), "text/markdown", StringComparison.Ordinal)
            && !defaultContent.TryGetProperty("url", out _)
            && defaultContent.TryGetProperty("content", out var inlineContent)
            && inlineContent.ValueKind == JsonValueKind.Object
            && inlineContent.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && text.GetString()!.Contains("# Azure DevOps Project Schema", StringComparison.Ordinal),
            "schema documentation markdown URL should be removed after materializing inline markdown content");
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

    [Fact]
    public async Task Populate_WhenRunTwiceThroughRepositoryPipeline_IsIdempotent()
    {
        var pipelineDataAccessLayer = new MergeProcessingDataAccessLayer(
            CreateValidatedDataAccessLayer(new InMemoryDataAccessLayer()));
        var schemaPopulator = new SchemaPopulator(pipelineDataAccessLayer);

        var firstPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            firstPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                firstPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var secondPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            secondPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                secondPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));
    }

    [Fact]
    public async Task Populate_WhenSeedEntityDiffers_UsesConcurrencyTagAndRestoresCanonicalData()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var pipelineDataAccessLayer = new MergeProcessingDataAccessLayer(
            CreateValidatedDataAccessLayer(inMemoryDataAccessLayer));
        var schemaPopulator = new SchemaPopulator(pipelineDataAccessLayer);

        var firstPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            firstPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                firstPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var defaultProfile = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .First(entity =>
                entity.Data is JsonElement data
                && data.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(["defaults", "profiles", "default"])));
        Assert.NotNull(defaultProfile.Data);
        Assert.NotNull(defaultProfile.ConcurrencyTag);

        var modifiedProfileData = this.WithUpdatedTheme(defaultProfile.Data.Value, "light");
        var driftResult = await pipelineDataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Introduce profile drift.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = defaultProfile.EntityId,
                        ConcurrencyTag = defaultProfile.ConcurrencyTag,
                        Data = modifiedProfileData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        Assert.DoesNotContain(driftResult.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);

        var secondPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            secondPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                secondPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var postPopulateResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var restoredProfile = postPopulateResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .First(entity => entity.EntityId == defaultProfile.EntityId);
        Assert.NotNull(restoredProfile.Data);
        Assert.True(
            restoredProfile.Data.Value.TryGetProperty("theme", out var restoredTheme)
            && restoredTheme.ValueKind == JsonValueKind.String
            && string.Equals(restoredTheme.GetString(), "dark", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Populate_WhenEntityTypesFolderIsDeletedInUnderlyingStore_RecreatesFolder()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var pipelineDataAccessLayer = new MergeProcessingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(inMemoryDataAccessLayer));
        var schemaPopulator = new SchemaPopulator(pipelineDataAccessLayer);

        var firstPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            firstPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                firstPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var firstExport = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var entityTypesFolder = firstExport.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .First(
                snapshot =>
                    snapshot.Data is JsonElement data
                    && IsFolderEntity(data)
                    && data.TryGetProperty("names", out var names)
                    && names.ValueKind == JsonValueKind.Array
                    && names.EnumerateArray().Any(
                        name =>
                        {
                            var entityName = name.TryReadEntityName();
                            return entityName is not null
                                && entityName.Value.Components.SequenceEqual(["entity-types"], StringComparer.Ordinal);
                        }));
        Assert.NotNull(entityTypesFolder.ConcurrencyTag);

        var directDeleteResult = await inMemoryDataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Delete entity-types folder directly in underlying store.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityTypesFolder.EntityId,
                        ConcurrencyTag = entityTypesFolder.ConcurrencyTag,
                        Data = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        Assert.DoesNotContain(directDeleteResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var afterDeleteExport = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var latestAfterDelete = afterDeleteExport.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .GroupBy(static snapshot => snapshot.EntityId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last());
        Assert.DoesNotContain(
            latestAfterDelete.Values,
            snapshot =>
                snapshot.Data is JsonElement data
                && data.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(
                    name =>
                    {
                        var entityName = name.TryReadEntityName();
                        return entityName is not null
                            && entityName.Value.Components.SequenceEqual(["entity-types"], StringComparer.Ordinal);
                    }));

        var secondPopulateErrors = await schemaPopulator.Populate();
        Assert.True(
            secondPopulateErrors.Count == 0,
            string.Join(
                Environment.NewLine,
                secondPopulateErrors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));

        var secondExport = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        var latestAfterRepopulate = secondExport.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .GroupBy(static snapshot => snapshot.EntityId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last());
        Assert.Contains(
            latestAfterRepopulate.Values,
            snapshot =>
                snapshot.Data is JsonElement data
                && IsFolderEntity(data)
                && data.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(
                    name =>
                    {
                        var entityName = name.TryReadEntityName();
                        return entityName is not null
                            && entityName.Value.Components.SequenceEqual(["entity-types"], StringComparer.Ordinal);
                    }));
    }

    private static IDataAccessLayer CreateValidatedDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
    {
        return new SchemaValidatingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(underlyingDataAccessLayer));
    }

    private static void CollectMissingInlineMarkdownPaths(
        JsonElement element,
        string jsonPath,
        ISet<string> knownEmbeddedMarkdownUrls,
        ICollection<string> missingInlineMarkdownPaths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("mime-type", out var mimeType)
                && mimeType.ValueKind == JsonValueKind.String
                && string.Equals(mimeType.GetString(), "text/markdown", StringComparison.Ordinal)
                && element.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(url.GetString())
                && url.GetString()!.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !url.GetString()!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.GetString()!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && knownEmbeddedMarkdownUrls.Contains(url.GetString()!))
            {
                var hasInlineTextContent =
                    element.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Object
                    && content.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(text.GetString());
                if (!hasInlineTextContent)
                {
                    missingInlineMarkdownPaths.Add(jsonPath);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectMissingInlineMarkdownPaths(property.Value, $"{jsonPath}.{property.Name}", knownEmbeddedMarkdownUrls, missingInlineMarkdownPaths);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var arrayIndex = 0;
        foreach (var item in element.EnumerateArray())
        {
            CollectMissingInlineMarkdownPaths(item, $"{jsonPath}[{arrayIndex}]", knownEmbeddedMarkdownUrls, missingInlineMarkdownPaths);
            arrayIndex++;
        }
    }

    private static bool IsFolderEntity(
        JsonElement? data)
    {
        return data is JsonElement entityData
            && entityData.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array
            && entityTypes.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "folder", StringComparison.Ordinal));
    }

    private JsonElement WithUpdatedTheme(
        JsonElement profileData,
        string themeName)
    {
        var profileNode = JsonNode.Parse(profileData.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("Expected profile object JSON.");
        profileNode["theme"] = themeName;
        using var document = JsonDocument.Parse(profileNode.ToJsonString());
        return document.RootElement.Clone();
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
    #pragma warning restore CS0618
}

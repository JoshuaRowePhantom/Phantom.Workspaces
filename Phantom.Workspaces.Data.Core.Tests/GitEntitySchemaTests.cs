using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class GitEntitySchemaTests
{
    private static readonly Assembly DataCoreAssembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;

    [Fact]
    public void GitRepository_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-repository.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-repository", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("clone-url", out _));
        Assert.True(properties.TryGetProperty("ssh-url", out _));
    }

    [Fact]
    public void GitPullRequest_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-pull-request.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-pull-request", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("source-branch", out _));
        Assert.True(properties.TryGetProperty("target-branch", out _));
        Assert.True(properties.TryGetProperty("source-commit", out _));
        Assert.True(properties.TryGetProperty("merge-commit", out _));
        Assert.True(properties.TryGetProperty("repository", out _));
    }

    [Fact]
    public void GitWorkItem_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-work-item.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-work-item", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("repository", out _));
        Assert.True(properties.TryGetProperty("related-pull-requests", out _));
    }

    [Fact]
    public async Task Populate_IncludesGitRepositoryEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-repository"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitPullRequestEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-pull-request"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitWorkItemEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-work-item"], StringComparer.Ordinal));
    }

    [Fact]
    public void GitSchema_HasDefsGitDefinition_WithExpectedSubFields()
    {
        using var document = LoadEmbeddedSchema("git.json");

        Assert.True(document.RootElement.TryGetProperty("$defs", out var defs), "git.json must have a $defs section");
        Assert.True(defs.TryGetProperty("git", out var gitDef), "$defs must contain a 'git' entry");
        Assert.True(gitDef.TryGetProperty("properties", out var properties), "$defs.git must have a 'properties' section");
        Assert.True(properties.TryGetProperty("branch", out _), "$defs.git.properties must contain 'branch'");
        Assert.True(properties.TryGetProperty("head-commit", out _), "$defs.git.properties must contain 'head-commit'");
        Assert.True(properties.TryGetProperty("remotes", out _), "$defs.git.properties must contain 'remotes'");
    }

    [Fact]
    public void GitSchema_PropertiesGit_IsRefToDefsGit()
    {
        using var document = LoadEmbeddedSchema("git.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("git", out var gitProperty));
        Assert.True(gitProperty.TryGetProperty("$ref", out var refValue), "git.json#/properties/git must be a $ref");
        Assert.Equal("#/$defs/git", refValue.GetString());
    }

    [Fact]
    public void GitWorktreeSchema_PropertiesGit_IsRefToGitSchemaDefsGit()
    {
        using var document = LoadEmbeddedSchema("git-worktree.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("git", out var gitProperty));
        Assert.True(gitProperty.TryGetProperty("$ref", out var refValue), "git-worktree.json#/properties/git must be a $ref");
        Assert.Equal("git.json#/$defs/git", refValue.GetString());
    }

    [Fact]
    public void GitWorktreeEntityTypeViewJson_Names_ContainsEntityTypeViewsGitWorktree()
    {
        using var document = LoadEmbeddedEntityTypeView("git-worktree-entity-type-view.json");

        Assert.True(document.RootElement.TryGetProperty("names", out var names));
        Assert.Equal(JsonValueKind.Array, names.ValueKind);
        var found = names.EnumerateArray().Any(static nameEntry =>
            nameEntry.ValueKind == JsonValueKind.Array
            && nameEntry.GetArrayLength() == 2
            && nameEntry[0].GetString() == "entity-type-views"
            && nameEntry[1].GetString() == "git-worktree");
        Assert.True(found, "names must contain [\"entity-type-views\", \"git-worktree\"]");
    }

    [Fact]
    public void GitWorktreeEntityTypeViewJson_IsEmbedded_AndHasExpectedFields()
    {
        using var document = LoadEmbeddedEntityTypeView("git-worktree-entity-type-view.json");

        Assert.True(document.RootElement.TryGetProperty("fields", out var fields));
        Assert.Equal(JsonValueKind.Array, fields.ValueKind);

        var fieldPaths = fields.EnumerateArray()
            .Where(static f => f.TryGetProperty("field-path", out _))
            .Select(static f => f.GetProperty("field-path").EnumerateArray()
                .Select(static p => p.GetString()!).ToArray())
            .ToArray();

        Assert.Contains(fieldPaths, static p => p.SequenceEqual(["path"]));
        Assert.Contains(fieldPaths, static p => p.SequenceEqual(["git", "branch"]));
        Assert.Contains(fieldPaths, static p => p.SequenceEqual(["git", "head-commit"]));
        Assert.Contains(fieldPaths, static p => p.SequenceEqual(["target-branch"]));
    }

    [Fact]
    public async Task Populate_IncludesGitWorktreeEntityTypeView()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(
            seededNames,
            n => n.SequenceEqual(["entity-type-views", "git-worktree"], StringComparer.Ordinal));
    }

    private static JsonDocument LoadEmbeddedEntityTypeView(string fileName)
    {
        var resourceName = $"Phantom.Workspaces.Data.JsonEntities.entity_type_views.{fileName}";
        using var stream = DataCoreAssembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream!);
    }

    private static JsonDocument LoadEmbeddedSchema(string fileName)
    {
        var resourceName = $"Phantom.Workspaces.Data.JsonSchemas.{fileName}";
        using var stream = DataCoreAssembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream!);
    }

    private static async Task<IReadOnlyCollection<string[]>> GetSeededNamesAsync()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = new SchemaValidatingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(inMemoryDataAccessLayer));
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);

        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(e => $"{e.RelatedEntityId?.Value}: {e.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        return exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Where(static data => data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            .SelectMany(static data => data.GetProperty("names").EnumerateArray())
            .Select(static name => name.TryReadEntityName())
            .Where(static name => name is not null)
            .Select(static name => name!.Value.Components)
            .ToArray();
    }
}

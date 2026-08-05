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
              "entity-types": ["entity", "note"],
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
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "invalid-manifest"]],
              "display-name": { "default": "Invalid" }
            }
            """);

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task GetValidationErrorsAsync_GitWorktreeEntityWithGitMetadata_ReturnsNoErrors()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["git-worktrees", "C:/dev/my-repo"]],
              "display-name": { "default": "my-repo" },
              "path": "C:/dev/my-repo",
              "git": {
                "branch": "main",
                "head-commit": "abc1234def5678901234567890123456789012345",
                "remotes": [
                  { "name": "origin", "url": "https://github.com/example/my-repo.git" }
                ]
              }
            }
            """);

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task GetValidationErrorsAsync_GitEntityWithGitMetadata_ReturnsNoErrors()
    {
        IEntitySchemaComposer composer = await CreatePopulatedComposerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "git"],
              "names": [["git", "C:/dev/my-repo"]],
              "display-name": { "default": "my-repo" },
              "path": "C:/dev/my-repo",
              "git": {
                "branch": "main",
                "head-commit": "abc1234def5678901234567890123456789012345",
                "remotes": [
                  { "name": "origin", "url": "https://github.com/example/my-repo.git" }
                ]
              }
            }
            """);

        var errors = await composer.GetValidationErrorsAsync(document.RootElement);

        Assert.Empty(errors);
    }

    [Fact]
    public void SchemaDefinitions_DoesNotDefineFilesystemFolderEntityType()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        Assert.Contains(
            resourceNames,
            name => name.EndsWith(".filesystem-path-entity-type.json", StringComparison.Ordinal));
        Assert.Contains(
            resourceNames,
            name => name.EndsWith(".folder-entity-type.json", StringComparison.Ordinal));
        Assert.DoesNotContain(
            resourceNames,
            name => name.Contains("filesystem-folder", StringComparison.Ordinal));
    }

    [Fact]
    public void GitWorkspaceScanToolDefaults_DocumentationDoesNotMentionFilesystemFolder()
    {
        var assembly = typeof(SchemaPopulator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("defaults.tools.git-workspace-scan-tool.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();

        Assert.DoesNotContain("filesystem-folder", text, StringComparison.Ordinal);
    }
}

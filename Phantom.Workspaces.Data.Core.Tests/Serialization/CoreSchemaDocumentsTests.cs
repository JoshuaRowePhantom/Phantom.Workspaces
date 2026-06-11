using Phantom.Workspaces.Data.Serialization;
using System.Text.Json;

namespace Phantom.Workspaces.Data.Tests.Serialization;

public sealed class CoreSchemaDocumentsTests
{
    [Fact]
    public void CoreLocalizedStringDocument_TryParse_ReadsDefault()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "default": "Hello",
              "fr-FR": "Bonjour"
            }
            """);

        var localizedStringDocument = CoreLocalizedStringDocument.Deserialize(document.RootElement);

        Assert.NotNull(localizedStringDocument);
        Assert.True(localizedStringDocument.IsValid());
        Assert.Equal("Hello", localizedStringDocument.GetValue("en-US"));
        Assert.Equal("Bonjour", localizedStringDocument.GetValue("fr-FR"));
    }

    [Fact]
    public void CoreLocalizedStringDocument_SetValue_UsesDefaultWhenLocaleMissing()
    {
        var localizedStringDocument = new CoreLocalizedStringDocument()
            .SetValue(null, "Default")
            .SetValue("en-US", "English");

        Assert.Equal("English", localizedStringDocument.GetValue("en-US"));
        Assert.Equal("Default", localizedStringDocument.GetValue("es-ES"));
    }

    [Fact]
    public void CoreEntityNameDocument_TryParse_ReadsComponents()
    {
        using var document = JsonDocument.Parse("""["documentation","getting-started"]""");

        var entityNameDocument = CoreEntityNameDocument.Deserialize(document.RootElement);

        Assert.NotNull(entityNameDocument);
        Assert.Equal(["documentation", "getting-started"], entityNameDocument.Components);
        Assert.Equal("[\"documentation\",\"getting-started\"]", entityNameDocument.ToCanonicalName());
    }

    [Fact]
    public void CoreEntityReferenceDocument_TryParse_ReadsEntityIdReference()
    {
        var entityId = Guid.NewGuid();
        using var document = JsonDocument.Parse($"\"{entityId:D}\"");

        var entityReferenceDocument = CoreEntityReferenceDocument.Deserialize(document.RootElement);

        Assert.NotNull(entityReferenceDocument);
        Assert.Equal(entityId.ToString("D"), entityReferenceDocument.EntityId);
        Assert.Null(entityReferenceDocument.EntityName);
    }

    [Fact]
    public void CoreEntityReferenceDocument_TryParse_ReadsEntityNameReference()
    {
        using var document = JsonDocument.Parse("""["entity-types","workspace"]""");

        var entityReferenceDocument = CoreEntityReferenceDocument.Deserialize(document.RootElement);

        Assert.NotNull(entityReferenceDocument);
        Assert.Null(entityReferenceDocument.EntityId);
        Assert.Equal(["entity-types", "workspace"], entityReferenceDocument.EntityName!.Components);
    }

    [Fact]
    public void CoreEntityTypeSetDocument_TryParse_ReadsEntityTypeNames()
    {
        using var document = JsonDocument.Parse("""["entity-type","note"]""");

        var entityTypeSetDocument = CoreEntityTypeSetDocument.Deserialize(document.RootElement);

        Assert.NotNull(entityTypeSetDocument);
        Assert.Equal(["entity-type", "note"], entityTypeSetDocument.EntityTypeNames);
    }

    [Fact]
    public void CoreTimestampDocument_TryParse_ReadsDateTimeAndChangeId()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "datetime": "2026-06-11T00:00:00Z",
              "change-id": "abc123"
            }
            """);

        var timestampDocument = CoreTimestampDocument.Deserialize(document.RootElement);

        Assert.NotNull(timestampDocument);
        Assert.True(timestampDocument.IsValid());
        Assert.Equal("2026-06-11T00:00:00Z", timestampDocument.DateTime);
        Assert.Equal("abc123", timestampDocument.ChangeId);
    }

    [Fact]
    public void CoreSortFieldDocument_TryParse_ReadsSortField()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "field-path": ["names","0"],
              "sort-direction": "ascending"
            }
            """);

        var sortFieldDocument = CoreSortFieldDocument.Deserialize(document.RootElement);

        Assert.NotNull(sortFieldDocument);
        Assert.True(sortFieldDocument.IsValid());
        Assert.Equal("ascending", sortFieldDocument.SortDirection);
    }
}

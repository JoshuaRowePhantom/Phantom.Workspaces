using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public sealed class DataAccessLayerJsonExtensionsTests
{
    [Fact]
    public void TryReadEntityName_WithValidStringArray_ReturnsEntityName()
    {
        var json = JsonDocument.Parse("[\"workspaces\",\"my-workspace\"]").RootElement;
        
        var result = json.TryReadEntityName();
        
        Assert.NotNull(result);
        Assert.Collection(result.Value.Components, 
            item => Assert.Equal("workspaces", item),
            item => Assert.Equal("my-workspace", item));
    }

    [Fact]
    public void TryReadEntityName_WithSingleComponent_ReturnsEntityName()
    {
        var json = JsonDocument.Parse("[\"views\"]").RootElement;
        
        var result = json.TryReadEntityName();
        
        Assert.NotNull(result);
        Assert.Single(result.Value.Components, "views");
    }

    [Fact]
    public void TryReadEntityName_WithEmptyArray_ReturnsRootEntityName()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = json.TryReadEntityName();
        
        Assert.NotNull(result);
        Assert.Empty(result.Value.Components);
    }

    [Fact]
    public void TryReadEntityName_WithStringElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("\"string\"").RootElement;
        
        var result = json.TryReadEntityName();
        
        Assert.Null(result);
    }

    [Fact]
    public void TryReadEntityName_WithNonStringNonArrayElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("{\"name\":\"string\"}").RootElement;

        var result = json.TryReadEntityName();

        Assert.Null(result);
    }

    [Fact]
    public void TryReadEntityName_SkipsNonStringElements()
    {
        var json = JsonDocument.Parse("[\"valid\", 123, \"also-valid\"]").RootElement;
        
        var result = json.TryReadEntityName();
        
        Assert.NotNull(result);
        Assert.Collection(result.Value.Components,
            item => Assert.Equal("valid", item),
            item => Assert.Equal("also-valid", item));
    }

    [Fact]
    public void TryReadEntityReference_WithGuidString_ReturnsEntityId()
    {
        var json = JsonDocument.Parse("\"5a48c0ee-4a39-4d1b-9c6c-c3de6e67ce27\"").RootElement;

        var result = json.TryReadEntityReference();

        Assert.NotNull(result);
        Assert.Equal(new EntityId("5a48c0ee-4a39-4d1b-9c6c-c3de6e67ce27"), result.Value.EntityId);
        Assert.Null(result.Value.EntityName);
        Assert.False(result.Value.IsNameArray);
    }

    [Fact]
    public void TryReadEntityReference_WithNameString_ReturnsNull()
    {
        var json = JsonDocument.Parse("\"docs/intro\"").RootElement;

        var result = json.TryReadEntityReference();

        Assert.Null(result);
    }

    [Fact]
    public void TryReadEntityReference_WithNameArray_ReturnsEntityName()
    {
        var json = JsonDocument.Parse("[\"docs\",\"intro\"]").RootElement;

        var result = json.TryReadEntityReference();

        Assert.NotNull(result);
        Assert.Null(result.Value.EntityId);
        Assert.Equal(new EntityName("docs", "intro"), result.Value.EntityName);
        Assert.True(result.Value.IsNameArray);
    }

    [Fact]
    public void TryReadEntityTypeNames_WithValidStringArray_ReturnsEntityTypeNames()
    {
        var json = JsonDocument.Parse("[\"entity\",\"workspace\"]").RootElement;
        
        var result = json.TryReadEntityTypeNames();
        
        Assert.NotNull(result);
        Assert.Collection(result.Value.Values,
            item => Assert.Equal("entity", item),
            item => Assert.Equal("workspace", item));
    }

    [Fact]
    public void TryReadEntityTypeNames_WithEmptyArray_ReturnsNull()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = json.TryReadEntityTypeNames();
        
        Assert.Null(result);
    }

    [Fact]
    public void TryReadEntityTypeNames_WithNonArrayElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("\"string\"").RootElement;
        
        var result = json.TryReadEntityTypeNames();
        
        Assert.Null(result);
    }

    [Fact]
    public void TryReadRelationshipTypeNames_WithValidStringArray_ReturnsRelationshipTypeNames()
    {
        var json = JsonDocument.Parse("[\"contains\",\"references\"]").RootElement;
        
        var result = json.TryReadRelationshipTypeNames();
        
        Assert.NotNull(result);
        Assert.Collection(result.Value.Values,
            item => Assert.Equal("contains", item),
            item => Assert.Equal("references", item));
    }

    [Fact]
    public void TryReadRelationshipTypeNames_WithEmptyArray_ReturnsNull()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = json.TryReadRelationshipTypeNames();
        
        Assert.Null(result);
    }

    [Fact]
    public void TryReadRoleNames_WithValidStringArray_ReturnsRoleNames()
    {
        var json = JsonDocument.Parse("[\"parent\",\"child\"]").RootElement;
        
        var result = json.TryReadRoleNames();
        
        Assert.NotNull(result);
        Assert.Collection(result.Value.Values,
            item => Assert.Equal("parent", item),
            item => Assert.Equal("child", item));
    }

    [Fact]
    public void TryReadRoleNames_WithEmptyArray_ReturnsNull()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = json.TryReadRoleNames();
        
        Assert.Null(result);
    }

    [Fact]
    public void ExtractStringArray_WithValidArray_ReturnsStrings()
    {
        var json = JsonDocument.Parse("""
            {
              "names": ["first", "second", "third"]
            }
        """).RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Collection(result,
            item => Assert.Equal("first", item),
            item => Assert.Equal("second", item),
            item => Assert.Equal("third", item));
    }

    [Fact]
    public void ExtractStringArray_WithMissingProperty_ReturnsEmptyArray()
    {
        var json = JsonDocument.Parse("{}").RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStringArray_WithNonArrayProperty_ReturnsEmptyArray()
    {
        var json = JsonDocument.Parse("""
            {
              "names": "not-an-array"
            }
        """).RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStringArray_WithEmptyArray_ReturnsEmptyArray()
    {
        var json = JsonDocument.Parse("""
            {
              "names": []
            }
        """).RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStringArray_SkipsNonStringElements()
    {
        var json = JsonDocument.Parse("""
            {
              "names": ["first", 123, "second", null, "third"]
            }
        """).RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Collection(result,
            item => Assert.Equal("first", item),
            item => Assert.Equal("second", item),
            item => Assert.Equal("third", item));
    }

    [Fact]
    public void ExtractStringArray_SkipsEmptyStrings()
    {
        var json = JsonDocument.Parse("""
            {
              "names": ["first", "", "second", "   ", "third"]
            }
        """).RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Collection(result,
            item => Assert.Equal("first", item),
            item => Assert.Equal("second", item),
            item => Assert.Equal("third", item));
    }

    [Fact]
    public void ExtractStringArray_WithNonObjectRoot_ReturnsEmptyArray()
    {
        var json = JsonDocument.Parse("[]").RootElement;
        
        var result = json.ExtractStringArray("names");
        
        Assert.Empty(result);
    }

    [Fact]
    public void JsonSerializer_EntityName_SerializesAsArray()
    {
        var entityName = new EntityName("documentation", "getting-started");

        var json = JsonSerializer.Serialize(entityName);

        Assert.Equal("""["documentation","getting-started"]""", json);
    }

    [Fact]
    public void JsonSerializer_EntityName_DeserializesFromArray()
    {
        var entityName = JsonSerializer.Deserialize<EntityName>("""["documentation","getting-started"]""");

        Assert.Equal(new EntityName("documentation", "getting-started"), entityName);
    }

    [Fact]
    public void JsonSerializer_EntityName_DeserializesEmptyArrayToRoot()
    {
        var entityName = JsonSerializer.Deserialize<EntityName>("[]");

        Assert.Equal(EntityName.Root, entityName);
    }

    [Fact]
    public void JsonSerializer_EntityName_DeserializationRejectsNonArray()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EntityName>("""{"components":["one"]}"""));
    }
}

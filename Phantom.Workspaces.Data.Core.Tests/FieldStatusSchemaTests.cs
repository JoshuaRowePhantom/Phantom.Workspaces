using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data.Tests;

public sealed class FieldStatusSchemaTests
{
    [Fact]
    public void CoreSchema_ExposesFieldStatusDefinitionWithStatusValueArrays()
    {
        using var coreSchema = LoadCoreSchema();

        var fieldStatusDefinition = GetFieldStatusDefinition(coreSchema.RootElement);

        Assert.True(fieldStatusDefinition.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("good-status-values", out var goodValues));
        Assert.True(properties.TryGetProperty("bad-status-values", out var badValues));
        Assert.Equal("array", goodValues.GetProperty("type").GetString());
        Assert.Equal("array", badValues.GetProperty("type").GetString());
        Assert.Equal("string", goodValues.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("string", badValues.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void FieldStatusDefinition_ValidatesWellFormedAnnotation()
    {
        var schema = LoadFieldStatusSchema();

        using var annotation = JsonDocument.Parse(
            """
            {
              "good-status-values": ["completed"],
              "bad-status-values": ["blocked", "cancelled"]
            }
            """);

        var result = schema.Evaluate(
            annotation.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void FieldStatusDefinition_RejectsNonArrayStatusValues()
    {
        var schema = LoadFieldStatusSchema();

        using var annotation = JsonDocument.Parse(
            """
            {
              "good-status-values": "completed"
            }
            """);

        var result = schema.Evaluate(
            annotation.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void FieldStatusDefinition_RejectsUnknownProperties()
    {
        var schema = LoadFieldStatusSchema();

        using var annotation = JsonDocument.Parse(
            """
            {
              "good-status-values": ["completed"],
              "unexpected": true
            }
            """);

        var result = schema.Evaluate(
            annotation.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });

        Assert.False(result.IsValid);
    }

    private static JsonSchema LoadFieldStatusSchema()
    {
        using var coreSchema = LoadCoreSchema();
        var fieldStatusDefinition = GetFieldStatusDefinition(coreSchema.RootElement);
        return JsonSchema.FromText(fieldStatusDefinition.GetRawText());
    }

    private static JsonElement GetFieldStatusDefinition(JsonElement coreSchemaRoot)
    {
        Assert.True(coreSchemaRoot.TryGetProperty("$defs", out var defs));
        Assert.True(defs.TryGetProperty("field-status", out var fieldStatusDefinition));
        return fieldStatusDefinition;
    }

    private static JsonDocument LoadCoreSchema()
    {
        var assembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("JsonSchemas.core.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return JsonDocument.Parse(stream);
    }
}

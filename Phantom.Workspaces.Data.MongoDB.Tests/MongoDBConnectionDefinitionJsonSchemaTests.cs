using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDBConnectionDefinitionJsonSchemaTests
{
    [Fact]
    public void Value_WhenValidContainerConnection_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "container",
              "container-name": "mongo-db",
              "data-directory": "C:\\mongo-data"
            }
            """);

        var result = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Value_WhenValidExternalConnection_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "external",
              "connection-string": "mongodb://localhost:27017"
            }
            """);

        var result = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Value_WhenRequiredPropertyIsMissing_IsInvalid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "container",
              "container-name": "mongo-db"
            }
            """);

        var result = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.False(result.IsValid);
    }

    private static JsonElement ParseElement(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

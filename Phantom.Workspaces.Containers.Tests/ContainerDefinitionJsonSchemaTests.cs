using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Containers.Tests;

public sealed class ContainerDefinitionJsonSchemaTests
{
    [Theory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("container")]
    public void Value_WhenValidContainerDefinition_IsValid(
        string networkType)
    {
        var instance = ParseElement(
            $$"""
            {
              "container-name": "mongo-db",
              "image-name": "mongo:latest",
              "network-type": "{{networkType}}",
              "environment-variables": {
                "MONGO_INITDB_ROOT_USERNAME": "root"
              },
              "mounts": [
                {
                  "source": "C:\\mongo-data",
                  "target": "/data/db",
                  "read-only": false
                }
              ]
            }
            """);

        var result = ContainerDefinitionJsonSchema.Value.Evaluate(
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
              "container-name": "mongo-db",
              "image-name": "mongo:latest",
              "network-type": "bridge",
              "environment-variables": {}
            }
            """);

        var result = ContainerDefinitionJsonSchema.Value.Evaluate(
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

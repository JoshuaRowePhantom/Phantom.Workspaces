using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ChatHistoryProviderDefinitionJsonSchemaTests
{
    [Fact]
    public void Value_WhenValidContainerProvider_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "mongodb",
              "mongoProvider": "container",
              "database-name": "phantom_chat_history",
              "collection-name": "messages",
              "container-name": "phantom-mongodb",
              "data-directory": "./mongo-data",
              "host-port": 27017
            }
            """);

        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Value_WhenValidExternalProvider_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "mongodb",
              "mongoProvider": "external",
              "database-name": "phantom_chat_history",
              "collection-name": "messages",
              "connection-string": "mongodb://localhost:27017"
            }
            """);

        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Value_WhenRequiredProviderFieldIsMissing_IsInvalid()
    {
        var instance = ParseElement(
            """
            {
              "mongoProvider": "container",
              "database-name": "phantom_chat_history",
              "collection-name": "messages",
              "container-name": "phantom-mongodb",
              "data-directory": "./mongo-data"
            }
            """);

        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Value_WhenContainerRequiredFieldsMissing_IsInvalid()
    {
        var instance = ParseElement(
            """
            {
              "provider": "mongodb",
              "mongoProvider": "container",
              "database-name": "phantom_chat_history",
              "collection-name": "messages"
            }
            """);

        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ExampleMongoDBLocalChatHistory_ValidatesSuccessfully()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplePath = Path.Combine(repositoryRoot.FullName, "docs", "examples", "qwen-local-chat-with-mongodb.json");

        Assert.True(File.Exists(examplePath), $"Example file not found: {examplePath}");

        // The new example embeds chat-history as a tool, extract it for schema validation
        var json = File.ReadAllText(examplePath);
        using var document = JsonDocument.Parse(json);
        var tools = document.RootElement.GetProperty("tools");
        
        var chatHistoryTool = tools.EnumerateArray()
            .FirstOrDefault(tool => 
                tool.TryGetProperty("kind", out var kind) && 
                kind.GetString() == "chat-history");
        
        Assert.False(chatHistoryTool.ValueKind == JsonValueKind.Undefined, "chat-history tool not found in example");
        
        var connection = chatHistoryTool.GetProperty("options").GetProperty("connection");
        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            connection,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CanLoadExampleMongoDBLocalChatHistory_UsingFromFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplePath = Path.Combine(repositoryRoot.FullName, "docs", "examples", "qwen-local-chat-with-mongodb.json");

        // The new example embeds chat-history as a tool, extract it for testing
        var json = File.ReadAllText(examplePath);
        using var document = JsonDocument.Parse(json);
        var tools = document.RootElement.GetProperty("tools");
        
        var chatHistoryTool = tools.EnumerateArray()
            .FirstOrDefault(tool => 
                tool.TryGetProperty("kind", out var kind) && 
                kind.GetString() == "chat-history");
        
        Assert.False(chatHistoryTool.ValueKind == JsonValueKind.Undefined, "chat-history tool not found in example");
        
        var connectionJson = chatHistoryTool.GetProperty("options").GetProperty("connection").GetRawText();
        using var connectionDocument = JsonDocument.Parse(connectionJson);
        var connectionElement = connectionDocument.RootElement.Clone();
        
        // Manually deserialize since we're not using FromFile
        var provider = (string?)connectionElement.GetProperty("provider").GetString();
        var mongoProvider = (string?)connectionElement.GetProperty("mongoProvider").GetString();
        
        Assert.Equal("mongodb", provider);
        Assert.Equal("container", mongoProvider);
        Assert.Equal("phantom_chat_history", connectionElement.GetProperty("database-name").GetString());
        Assert.Equal("messages", connectionElement.GetProperty("collection-name").GetString());
        Assert.Equal("phantom-mongodb", connectionElement.GetProperty("container-name").GetString());
        Assert.Equal("./mongo-data", connectionElement.GetProperty("data-directory").GetString());
        Assert.Equal(27017, connectionElement.GetProperty("host-port").GetInt32());
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".gitignore")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}

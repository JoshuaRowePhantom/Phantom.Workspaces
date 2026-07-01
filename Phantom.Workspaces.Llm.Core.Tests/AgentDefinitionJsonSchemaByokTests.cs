using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Schema validation tests for BYOK (bring-your-own-key) fields added to the
/// <c>connection</c> object in <c>AgentDefinition.json</c>.
/// </summary>
public sealed class AgentDefinitionJsonSchemaByokTests
{
    private static readonly EvaluationOptions EvalOptions = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        RequireFormatValidation = false,
    };

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    private static bool Evaluate(string json)
    {
        return AgentDefinitionJsonSchema.Value
            .Evaluate(Parse(json), EvalOptions)
            .IsValid;
    }

    [Fact]
    public void AgentDefinitionSchema_GithubCopilot_WithByokEndpoint_IsValid()
    {
        Assert.True(Evaluate("""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": {
              "kind": "key",
              "endpoint": "http://localhost:1234/",
              "apiKey": "test-key"
            }
          }
        }
        """));
    }

    [Fact]
    public void AgentDefinitionSchema_GithubCopilot_WithAllByokFields_IsValid()
    {
        Assert.True(Evaluate("""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": {
              "kind": "key",
              "endpoint": "http://localhost:1234/",
              "apiKey": "test-key",
              "providerType": "openai",
              "wireApi": "chat-completions",
              "wireModel": "gpt-4-wire",
              "headers": { "X-Custom": "value" }
            }
          }
        }
        """));
    }

    [Fact]
    public void AgentDefinitionSchema_GithubCopilot_WithByokHeaders_StringValues_IsValid()
    {
        Assert.True(Evaluate("""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": {
              "kind": "key",
              "endpoint": "http://localhost:1234/",
              "headers": { "X-Token": "secret", "X-Region": "us-east-1" }
            }
          }
        }
        """));
    }

    [Fact]
    public void AgentDefinitionSchema_GithubCopilot_WithByokHeaders_NonStringValue_IsInvalid()
    {
        Assert.False(Evaluate("""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": {
              "kind": "key",
              "endpoint": "http://localhost:1234/",
              "headers": { "X-Retry": 3 }
            }
          }
        }
        """));
    }
}

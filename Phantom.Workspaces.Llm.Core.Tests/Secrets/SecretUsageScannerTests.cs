using System.Collections.Generic;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class SecretUsageScannerTests
{
    private static AgentDefinition Load(string json)
        => AgentDefinition.FromJson(json)
            ?? throw new System.InvalidOperationException("Failed to load agent definition for test.");

    private static AgentDefinition WithModelSecret(string secret = "${SECRET:ModelSecret}") => Load($$"""
    {
      "kind": "prompt",
      "name": "test-agent",
      "model": {
        "id": "m",
        "provider": "p",
        "apiType": "OpenAI",
        "options": { "additionalProperties": { "ApiToken": "{{secret}}", "Other": "nothing" } }
      }
    }
    """);

    [Fact]
    public void Scan_NoPlaceholders_ReturnsEmpty()
    {
        var definition = Load("""
        { "kind": "prompt", "name": "no-secrets", "instructions": "hello world" }
        """);

        var usages = new SecretUsageScanner().Scan(definition);

        Assert.Empty(usages);
    }

    [Fact]
    public void Scan_ModelOptionsContainsSecret_ReturnsOneUsageWithModelPath()
    {
        var usages = new SecretUsageScanner().Scan(WithModelSecret());

        var usage = Assert.Single(usages);
        Assert.Equal("ModelSecret", usage.SecretName);
        Assert.Equal("definition.model.options.additionalProperties.ApiToken", usage.JsonPath);
    }

    [Fact]
    public void Scan_ToolOptionsContainsSecret_ReturnsUsageWithToolPath()
    {
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "tools": [ { "name": "t0", "kind": "mcp", "description": "tool ${SECRET:ToolSecret}" } ]
        }
        """);

        var usage = Assert.Single(new SecretUsageScanner().Scan(definition));

        Assert.Equal("ToolSecret", usage.SecretName);
        // The AgentSchema serializer emits tools as an object keyed by tool name.
        Assert.Equal("definition.tools.t0.description", usage.JsonPath);
    }

    [Fact]
    public void Scan_SystemPromptContainsSecret_ReturnsUsageWithSystemPromptPath()
    {
        var definition = Load("""
        { "kind": "prompt", "name": "test-agent", "instructions": "Use ${SECRET:SysSecret} now" }
        """);

        var usage = Assert.Single(new SecretUsageScanner().Scan(definition));

        Assert.Equal("SysSecret", usage.SecretName);
        Assert.Equal("definition.instructions", usage.JsonPath);
    }

    [Fact]
    public void Scan_ArbitraryStringField_ReturnsUsageWithFullJsonPath()
    {
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "additionalInstructions": "extra ${SECRET:ArbitrarySecret}"
        }
        """);

        var usage = Assert.Single(new SecretUsageScanner().Scan(definition));

        Assert.Equal("ArbitrarySecret", usage.SecretName);
        Assert.Equal("definition.additionalInstructions", usage.JsonPath);
    }

    [Fact]
    public void Scan_NestedArrayString_ReturnsUsageWithArrayIndexInPath()
    {
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": {
            "id": "m",
            "provider": "p",
            "apiType": "OpenAI",
            "options": { "stopSequences": [ "first", "second ${SECRET:NestedSecret}" ] }
          }
        }
        """);

        var usage = Assert.Single(new SecretUsageScanner().Scan(definition));

        Assert.Equal("NestedSecret", usage.SecretName);
        Assert.Equal("definition.model.options.stopSequences[1]", usage.JsonPath);
    }

    [Fact]
    public void Scan_SameSecretUsedTwice_ReturnsTwoUsagesWithDistinctPaths()
    {
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "instructions": "one ${SECRET:Shared}",
          "additionalInstructions": "two ${SECRET:Shared}"
        }
        """);

        var usages = new SecretUsageScanner().Scan(definition);

        Assert.Equal(2, usages.Count);
        Assert.All(usages, usage => Assert.Equal("Shared", usage.SecretName));
        Assert.Equal(2, usages.Select(usage => usage.JsonPath).Distinct().Count());
    }

    [Fact]
    public void Scan_MixedEnvVarAndSecretPlaceholders_OnlyReturnsSecrets()
    {
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "instructions": "env ${VAR} and ${GITHUB_TOKEN} and secret ${SECRET:OnlyMe}"
        }
        """);

        var usage = Assert.Single(new SecretUsageScanner().Scan(definition));

        Assert.Equal("OnlyMe", usage.SecretName);
    }

    [Fact]
    public void RewritePlaceholders_SubstitutesHandleTokenNotPlaintext()
    {
        var scanner = new SecretUsageScanner();
        var definition = WithModelSecret();
        var usage = Assert.Single(scanner.Scan(definition));

        var map = new Dictionary<SecretUsage, string> { [usage] = "handle-123" };
        scanner.RewritePlaceholders(definition, map);

        var json = definition.ToJson();
        Assert.Contains("${SECRET:handle-123}", json);
        Assert.DoesNotContain("${SECRET:ModelSecret}", json);
    }

    [Fact]
    public void RewritePlaceholders_ReplacesInModelOptions()
    {
        var scanner = new SecretUsageScanner();
        var definition = WithModelSecret();
        var usage = Assert.Single(scanner.Scan(definition));

        scanner.RewritePlaceholders(definition, new Dictionary<SecretUsage, string> { [usage] = "h1" });

        var rescanned = Assert.Single(scanner.Scan(definition));
        Assert.Equal("h1", rescanned.SecretName);
        Assert.Equal("definition.model.options.additionalProperties.ApiToken", rescanned.JsonPath);
    }

    [Fact]
    public void RewritePlaceholders_ReplacesInToolOptions()
    {
        var scanner = new SecretUsageScanner();
        var definition = Load("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "tools": [ { "name": "t0", "kind": "mcp", "description": "tool ${SECRET:ToolSecret}" } ]
        }
        """);
        var usage = Assert.Single(scanner.Scan(definition));

        scanner.RewritePlaceholders(definition, new Dictionary<SecretUsage, string> { [usage] = "tool-handle" });

        var json = definition.ToJson();
        Assert.Contains("${SECRET:tool-handle}", json);
        Assert.DoesNotContain("ToolSecret", json);
    }

    [Fact]
    public void RewritePlaceholders_ReplacesInSystemPrompt()
    {
        var scanner = new SecretUsageScanner();
        var definition = Load("""
        { "kind": "prompt", "name": "test-agent", "instructions": "Use ${SECRET:SysSecret} now" }
        """);
        var usage = Assert.Single(scanner.Scan(definition));

        scanner.RewritePlaceholders(definition, new Dictionary<SecretUsage, string> { [usage] = "sys-handle" });

        var json = definition.ToJson();
        Assert.Contains("${SECRET:sys-handle}", json);
        Assert.DoesNotContain("SysSecret", json);
    }

    [Fact]
    public void RewritePlaceholders_NeverIntroducesPlaintextSecret()
    {
        var scanner = new SecretUsageScanner();
        var definition = WithModelSecret();
        var usage = Assert.Single(scanner.Scan(definition));

        // The map value is an opaque handle, never a secret value.
        scanner.RewritePlaceholders(definition, new Dictionary<SecretUsage, string> { [usage] = "opaque-handle" });

        var json = definition.ToJson();
        // Every remaining ${SECRET:...} token references the opaque handle only.
        foreach (var rescan in scanner.Scan(definition))
        {
            Assert.Equal("opaque-handle", rescan.SecretName);
        }

        Assert.DoesNotContain("${SECRET:ModelSecret}", json);
    }
}

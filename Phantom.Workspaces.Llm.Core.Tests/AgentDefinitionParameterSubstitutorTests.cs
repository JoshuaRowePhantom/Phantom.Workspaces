using System.Collections.Generic;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionParameterSubstitutorTests
{
    [Fact]
    public void Substitute_WithNoParameters_ReturnsClonedTemplate()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, null);

        Assert.NotNull(definition);
        Assert.IsType<PromptAgent>(definition);
    }

    [Fact]
    public void Substitute_WithProvidedValue_SubstitutesPlaceholder()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "C:\\Projects\\MyApp" });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal("C:\\Projects\\MyApp", promptAgent.Model?.Options?.AdditionalProperties?["working-directory"]);
    }

    [Fact]
    public void Substitute_WithDefaultValue_UsesDefault()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false, "default": "C:\\Default" }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, null);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal("C:\\Default", promptAgent.Model?.Options?.AdditionalProperties?["working-directory"]);
    }

    [Fact]
    public void Substitute_RequiredParameterWithNoValue_ThrowsArgumentException()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        Assert.Throws<ArgumentException>(() =>
            AgentDefinitionParameterSubstitutor.Substitute(manifest, null));
    }

    [Fact]
    public void Substitute_WithExecutorParameter_DoesNotRequireOrSubstituteExecutorSelection()
    {
        // A required 'executor' parameter is a structured launch-time selection recorded in the typed
        // parameter-selections map (#1434, M7). It never flows through the string->string
        // parameter-values map, so the substitutor must NOT throw for a missing text value and must NOT
        // treat it as a ${param} substitution — while text/directory parameters are unchanged.
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true },
              { "name": "worker-executor", "kind": "executor", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        // Only the text parameter is supplied; the required executor parameter is deliberately omitted.
        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "C:\\Projects\\MyApp" });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal(
            "C:\\Projects\\MyApp",
            promptAgent.Model?.Options?.AdditionalProperties?["working-directory"]);
    }

    [Fact]
    public void AgentDefinitionParameterSubstitutor_WorkingDirectory_SubstitutedIntoModelOptions()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "C:\\dev\\myrepo" });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal("C:\\dev\\myrepo", promptAgent.Model?.Options?.AdditionalProperties?["working-directory"]?.ToString());
    }

    [Fact]
    public void AgentDefinitionParameterSubstitutor_WorkingDirectory_OmittedWhenNotProvided()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, null);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        var additionalProps = promptAgent.Model?.Options?.AdditionalProperties;
        Assert.True(
            additionalProps is null
            || !additionalProps.ContainsKey("working-directory")
            || additionalProps["working-directory"] is null
            || string.IsNullOrEmpty(additionalProps["working-directory"]?.ToString()),
            "working-directory should not be substituted when no value is provided");
    }

    [Fact]
    public void Substitute_WithUnknownParameterInDictionary_IgnoresIt()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var result = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string>
            {
                ["working-directory"] = "C:\\Projects",
                ["extra-key"] = "should-be-ignored",
            });

        var promptAgent = Assert.IsType<PromptAgent>(result);
        Assert.Equal("C:\\Projects", promptAgent.Model?.Options?.AdditionalProperties?["working-directory"]);
        Assert.False(
            promptAgent.Model?.Options?.AdditionalProperties?.ContainsKey("extra-key") == true,
            "Extra key not declared in the manifest should not appear in the substituted output");
    }

    [Fact]
    public void Substitute_OptionalParameterWithEmptyStringValue_RemovesKey()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "" });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.False(
            promptAgent.Model?.Options?.AdditionalProperties?.ContainsKey("working-directory") == true,
            "working-directory key should be removed when provided value is empty string");
    }

    [Fact]
    public void Substitute_OptionalParameterWithWhitespaceValue_RemovesKey()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": false }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        var definition = AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "   " });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.False(
            promptAgent.Model?.Options?.AdditionalProperties?.ContainsKey("working-directory") == true,
            "working-directory key should be removed when provided value is whitespace");
    }

    [Fact]
    public void Substitute_RequiredParameterWithEmptyStringValue_ThrowsArgumentException()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        Assert.Throws<ArgumentException>(() =>
            AgentDefinitionParameterSubstitutor.Substitute(
                manifest,
                new Dictionary<string, string> { ["working-directory"] = "" }));
    }

    [Fact]
    public void Substitute_DoesNotMutateManifestTemplate()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "test",
          "displayName": "Test",
          "parameters": {
            "properties": [
              { "name": "working-directory", "kind": "string", "required": true }
            ]
          },
          "template": {
            "kind": "prompt",
            "name": "test-agent",
            "model": {
              "id": "echo",
              "provider": "echo",
              "apiType": "Echo",
              "options": {
                "additionalProperties": {
                  "working-directory": "${working-directory}"
                }
              }
            }
          }
        }
        """);

        AgentDefinitionParameterSubstitutor.Substitute(
            manifest,
            new Dictionary<string, string> { ["working-directory"] = "C:\\Projects" });

        var template = Assert.IsType<PromptAgent>(manifest.Template);
        Assert.Equal("${working-directory}", template.Model?.Options?.AdditionalProperties?["working-directory"]);
    }
}

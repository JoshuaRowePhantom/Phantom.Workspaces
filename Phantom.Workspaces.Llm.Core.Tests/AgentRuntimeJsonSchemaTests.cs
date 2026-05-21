using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentRuntimeJsonSchemaTests
{
    [Fact]
    public void Value_WhenDomainUsesContainerHost_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "trustDomains": {
                "host": {
                  "container": "host",
                  "availableServers": {
                    "filesystem": {
                      "kind": "stdio",
                      "command": "npx",
                      "args": ["-y", "@modelcontextprotocol/server-filesystem"],
                      "stdioMcpServerTag": "stdio-mcp-server: filesystem"
                    },
                    "remote-sse": {
                      "kind": "sse",
                      "url": "https://example.org/mcp/sse"
                    }
                  }
                }
              }
            }
            """);

        var result = AgentRuntimeJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Value_WhenDomainContainsLegacyContainerDefinitionProperty_IsInvalid()
    {
        var instance = ParseElement(
            """
            {
              "trustDomains": {
                "host": {
                  "container": "host",
                  "containerDefinition": {
                    "baseContainerReference": "mcr.microsoft.com/dotnet/sdk:10.0"
                  }
                }
              }
            }
            """);

        var result = AgentRuntimeJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Value_WhenContainerDomainHasDockerOptions_IsValid()
    {
        var instance = ParseElement(
            """
            {
              "trustDomains": {
                "host": {
                  "container": "host"
                },
                "sandbox": {
                  "container": {
                    "baseContainerReference": "ghcr.io/example/agent-runtime:latest",
                    "dockerOptions": {
                      "user": "1000:1000",
                      "capAdd": ["SYS_PTRACE"]
                    },
                    "mountPoints": [
                      {
                        "type": "bind",
                        "source": "/workspace",
                        "target": "/app/workspace",
                        "readOnly": false,
                        "consistency": "delegated",
                        "bind": {
                          "createHostPath": true
                        }
                      }
                    ],
                    "networkConfiguration": {
                      "networkMode": "bridge",
                      "aliases": ["agent"],
                      "driverOpts": {
                        "com.docker.network.bridge.host_binding_ipv4": "127.0.0.1"
                      }
                    }
                  },
                  "availableServers": {
                    "planner-http": {
                      "kind": "http",
                      "url": "https://planner.example.org/mcp"
                    }
                  }
                }
              }
            }
            """);

        var result = AgentRuntimeJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    private static JsonElement ParseElement(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

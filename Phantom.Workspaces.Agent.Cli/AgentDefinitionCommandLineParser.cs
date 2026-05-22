using System.CommandLine;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Cli;

public sealed class AgentDefinitionCommandLineParser
{
    private readonly Option<string> providerOption = new("--provider", ["-p"])
    {
        Description = "LLM provider: 'echo' (default), 'ollama-local', or 'ollama-remote'",
        DefaultValueFactory = _ => "echo",
    };

    private readonly Option<string?> ollamaUrlOption = new("--ollama-url", ["-u"])
    {
        Description = "URL for remote Ollama instance (e.g., http://192.168.1.100:11434)",
    };

    private readonly Option<string?> ollamaModelOption = new("--model", ["-m"])
    {
        Description = "Model name for Ollama (e.g., 'mistral', 'llama2')",
    };

    private readonly Option<string?> thinkingOption = new("--think", ["--thinking"])
    {
        Description = "Thinking level: true/on/high, medium, low, false/off/none",
    };

    private readonly Option<string?> agentSchemaOption = new("--agent-schema", ["-s"])
    {
        Description = "Path to AgentSchema definition file (.json or .yaml) to load an agent from schema",
    };

    private readonly Option<bool> logChatOption = new("--log-chat")
    {
        Description = "Log chat messages sent to and received from IChatClient",
        DefaultValueFactory = _ => false,
    };

    private readonly Option<bool> logHttpRequestsOption = new("--log-http-requests")
    {
        Description = "Log provider HTTP requests/responses when supported (currently Ollama)",
        DefaultValueFactory = _ => false,
    };

    public void AddOptions(Command command)
    {
        command.Add(this.providerOption);
        command.Add(this.ollamaUrlOption);
        command.Add(this.ollamaModelOption);
        command.Add(this.thinkingOption);
        command.Add(this.agentSchemaOption);
        command.Add(this.logChatOption);
        command.Add(this.logHttpRequestsOption);
    }

    public AgentDefinitionParseResult Parse(ParseResult parseResult)
    {
        var thinking = NormalizeThinkingSetting(parseResult.GetValue(this.thinkingOption) ?? "high");
        var agentSchemaPath = parseResult.GetValue(this.agentSchemaOption);
        var logChat = parseResult.GetValue(this.logChatOption);
        var logHttpRequests = parseResult.GetValue(this.logHttpRequestsOption);
        var provider = parseResult.GetValue(this.providerOption)!;
        var ollamaUrl = parseResult.GetValue(this.ollamaUrlOption);
        var ollamaModel = parseResult.GetValue(this.ollamaModelOption);

        AgentDefinition definition;

        if (!string.IsNullOrWhiteSpace(agentSchemaPath))
        {
            definition = AgentDefinitionLoader.LoadAgent(agentSchemaPath);
        }
        else
        {
            definition = provider.ToLowerInvariant() switch
            {
                "echo" => BuildEchoDefinition(thinking),
                "ollama-local" => BuildOllamaDefinition("http://localhost:11434", ollamaModel, thinking),
                "ollama-remote" when !string.IsNullOrWhiteSpace(ollamaUrl) => BuildOllamaDefinition(ollamaUrl!, ollamaModel, thinking),
                "ollama-remote" => throw new InvalidOperationException(
                    "ollama-remote provider requires --ollama-url option. Example: --provider ollama-remote --ollama-url http://192.168.1.100:11434"),
                _ => throw new InvalidOperationException($"Unknown provider: {provider}. Use 'echo', 'ollama-local', or 'ollama-remote'.")
            };
        }

        return new AgentDefinitionParseResult(
            definition,
            agentSchemaPath,
            logChat,
            logHttpRequests,
            parseResult.UnmatchedTokens.ToArray());
    }

    private static AgentDefinition BuildEchoDefinition(string thinking)
    {
        var json = JsonSerializer.Serialize(new
        {
            kind = "prompt",
            name = "cli-echo-agent",
            description = "CLI default echo agent definition",
            instructions = "Echo user input.",
            model = new
            {
                id = "echo",
                provider = "echo",
                apiType = "Echo",
                options = new
                {
                    additionalProperties = new
                    {
                        thinking,
                    },
                },
            },
            tools = Array.Empty<object>(),
        });

        return AgentDefinitionLoader.LoadAgentFromJson(json);
    }

    private static AgentDefinition BuildOllamaDefinition(string endpoint, string? model, string thinking)
    {
        var modelId = string.IsNullOrWhiteSpace(model) ? "mistral" : model;

        var json = JsonSerializer.Serialize(new
        {
            kind = "prompt",
            name = "cli-ollama-agent",
            description = "CLI constructed Ollama agent definition",
            instructions = "You are a helpful assistant.",
            model = new
            {
                id = modelId,
                provider = "ollama",
                apiType = "Ollama",
                connection = new
                {
                    kind = "Anonymous",
                    endpoint,
                },
                options = new
                {
                    additionalProperties = new
                    {
                        thinking,
                    },
                },
            },
            tools = Array.Empty<object>(),
        });

        return AgentDefinitionLoader.LoadAgentFromJson(json);
    }

    private static string NormalizeThinkingSetting(string value) => value.ToLowerInvariant() switch
    {
        "true" or "on" or "high" => "high",
        "medium" or "med" => "medium",
        "low" => "low",
        "false" or "off" or "none" => "none",
        _ => throw new InvalidOperationException($"Unknown thinking level '{value}'. Use: true, false, low, medium, high"),
    };
}

public sealed record AgentDefinitionParseResult(
    AgentDefinition AgentDefinition,
    string? AgentSchemaPath,
    bool LogChat,
    bool LogHttpRequests,
    IReadOnlyList<string> UnmatchedArguments);

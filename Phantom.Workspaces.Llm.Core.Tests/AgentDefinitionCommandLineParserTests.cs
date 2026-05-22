using AgentSchema;
using System.CommandLine;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionCommandLineParserTests
{
    [Fact]
    public void Parse_DefaultArgs_ReturnsEchoAgentDefinition()
    {
        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, Array.Empty<string>());

        var result = parser.Parse(parseResult);
        var prompt = Assert.IsType<PromptAgent>(result.AgentDefinition);
        var additionalProperties = prompt.Model?.Options?.AdditionalProperties;

        Assert.Null(result.AgentSchemaPath);
        Assert.False(result.LogChat);
        Assert.False(result.LogHttpRequests);
        Assert.Equal("echo", prompt.Model?.Provider);
        Assert.Equal("echo", prompt.Model?.Id);
        Assert.NotNull(additionalProperties);
        Assert.True(additionalProperties!.TryGetValue("thinking", out var thinkingValue));
        Assert.Equal("high", thinkingValue);
    }

    [Fact]
    public void Parse_TestProviderArgs_ReturnsConstructedTestDefinition()
    {
        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, ["--provider", "test", "--think", "low"]);

        var result = parser.Parse(parseResult);
        var prompt = Assert.IsType<PromptAgent>(result.AgentDefinition);
        var additionalProperties = prompt.Model?.Options?.AdditionalProperties;

        Assert.Equal("echo", prompt.Model?.Provider);
        Assert.Equal("test", prompt.Model?.Id);
        Assert.NotNull(additionalProperties);
        Assert.True(additionalProperties!.TryGetValue("thinking", out var thinkingValue));
        Assert.Equal("low", thinkingValue);
    }

    [Fact]
    public void Parse_OllamaRemoteArgs_ReturnsConstructedOllamaDefinition()
    {
        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, ["--provider", "ollama-remote", "--ollama-url", "http://127.0.0.1:11434", "--model", "qwen3.6", "--think", "low", "--log-chat", "--log-http-requests"]);

        var result = parser.Parse(parseResult);
        var prompt = Assert.IsType<PromptAgent>(result.AgentDefinition);
        var connection = Assert.IsType<AnonymousConnection>(prompt.Model?.Connection);
        var additionalProperties = prompt.Model?.Options?.AdditionalProperties;

        Assert.Equal("ollama", prompt.Model?.Provider);
        Assert.Equal("qwen3.6", prompt.Model?.Id);
        Assert.Equal("http://127.0.0.1:11434", connection.Endpoint);
        Assert.True(result.LogChat);
        Assert.True(result.LogHttpRequests);
        Assert.NotNull(additionalProperties);
        Assert.True(additionalProperties!.TryGetValue("thinking", out var thinkingValue));
        Assert.Equal("low", thinkingValue);
    }

    [Fact]
    public void Parse_AgentSchemaPath_LoadsAgentDefinitionFromFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var schemaPath = Path.Combine(repositoryRoot.FullName, "docs", "examples", "qwen-local-chat.json");

        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, ["--agent-schema", schemaPath, "--think", "none"]);

        var result = parser.Parse(parseResult);
        var prompt = Assert.IsType<PromptAgent>(result.AgentDefinition);

        Assert.Equal(schemaPath, result.AgentSchemaPath);
        Assert.Equal("qwen-local-chat", prompt.Name);
    }

    [Fact]
    public void Parse_OllamaRemoteWithoutUrl_Throws()
    {
        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, ["--provider", "ollama-remote"]);

        var ex = Assert.Throws<InvalidOperationException>(() => parser.Parse(parseResult));
        Assert.Contains("requires --ollama-url option", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownProvider_Throws()
    {
        var parser = new AgentDefinitionCommandLineParser();
        var parseResult = Parse(parser, ["--provider", "not-real"]);

        var ex = Assert.Throws<InvalidOperationException>(() => parser.Parse(parseResult));
        Assert.Contains("Unknown provider", ex.Message, StringComparison.Ordinal);
    }

    private static ParseResult Parse(AgentDefinitionCommandLineParser parser, string[] args)
    {
        var command = new RootCommand();
        parser.AddOptions(command);
        return command.Parse(args);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}

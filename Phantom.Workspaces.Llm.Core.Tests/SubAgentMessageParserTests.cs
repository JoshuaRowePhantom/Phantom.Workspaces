using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class SubAgentMessageParserTests
{
    private static AgentDefinition CreateAgentDefinition(string name)
    {
        return AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "{{name}}",
          "model": { "id": "echo", "provider": "echo" }
        }
        """);
    }

    private static AgentDefinitionTool Tool(string name) => new()
    {
        Name = name,
        Description = $"The {name} sub-agent",
        Definition = CreateAgentDefinition(name),
    };

    private static SubAgentMessageParser CreateParser(params AgentDefinitionTool[] tools)
        => new(new SubAgentDispatcherOptions { AgentDefinitionTools = tools });

    [Fact]
    public void New_UsesDefaultDefinition_DefersSlug()
    {
        var parser = CreateParser(Tool("default"), Tool("foo"));

        var result = parser.Parse("new: File a bug about the parser");

        var create = Assert.IsType<CreateSubAgentInstruction>(result);
        Assert.Equal("default", create.Definition.Name);
        Assert.Equal("File a bug about the parser", create.Prompt);
        Assert.Null(create.ExplicitId);
        Assert.False(create.PrefixSlugWithDefinitionName);
    }

    [Fact]
    public void New_WithNoDefaultEntry_UsesFirstEntry()
    {
        var parser = CreateParser(Tool("foo"), Tool("bar"));

        var result = parser.Parse("new: do something");

        var create = Assert.IsType<CreateSubAgentInstruction>(result);
        Assert.Equal("foo", create.Definition.Name);
    }

    [Fact]
    public void NewWithDefinition_PrefixesSlugWithDefinitionName()
    {
        var parser = CreateParser(Tool("foo"), Tool("bar"));

        var result = parser.Parse("new(foo): investigate the widget");

        var create = Assert.IsType<CreateSubAgentInstruction>(result);
        Assert.Equal("foo", create.Definition.Name);
        Assert.Equal("investigate the widget", create.Prompt);
        Assert.Null(create.ExplicitId);
        Assert.True(create.PrefixSlugWithDefinitionName);
    }

    [Fact]
    public void NewWithDefinitionAndId_UsesExplicitId()
    {
        var parser = CreateParser(Tool("foo"), Tool("bar"));

        var result = parser.Parse("new(foo blammo): investigate the widget");

        var create = Assert.IsType<CreateSubAgentInstruction>(result);
        Assert.Equal("foo", create.Definition.Name);
        Assert.Equal("blammo", create.ExplicitId);
        Assert.False(create.PrefixSlugWithDefinitionName);
        Assert.Equal("investigate the widget", create.Prompt);
    }

    [Fact]
    public void RouteToSubAgent_ByExplicitId()
    {
        var parser = CreateParser(Tool("foo"));

        var result = parser.Parse("foo-bar-baz: continue please");

        var route = Assert.IsType<RouteToSubAgentInstruction>(result);
        Assert.Equal("foo-bar-baz", route.Id);
        Assert.Equal("continue please", route.Message);
    }

    [Fact]
    public void BareColon_RoutesToMostRecent_WhenAvailable()
    {
        var parser = CreateParser(Tool("foo"));

        var result = parser.Parse(": keep going", mostRecentlyDispatchedId: "foo-earlier");

        var route = Assert.IsType<RouteToMostRecentInstruction>(result);
        Assert.Equal("foo-earlier", route.Id);
        Assert.Equal("keep going", route.Message);
    }

    [Fact]
    public void BareColon_WithNoPriorDispatch_YieldsNoSubAgentError()
    {
        var parser = CreateParser(Tool("foo"));

        var result = parser.Parse(": keep going", mostRecentlyDispatchedId: null);

        var error = Assert.IsType<ParseErrorInstruction>(result);
        Assert.Equal(SubAgentMessageParser.NoSubAgentDispatchedMessage, error.Message);
    }

    [Fact]
    public void UnknownDefinition_YieldsErrorWithAvailableNames()
    {
        var parser = CreateParser(Tool("foo"), Tool("bar"));

        var result = parser.Parse("new(baz): do a thing");

        var error = Assert.IsType<ParseErrorInstruction>(result);
        Assert.Equal("Unknown agent definition 'baz'. Available: foo, bar.", error.Message);
    }

    [Fact]
    public void UnknownDefinition_WithExplicitId_StillYieldsError()
    {
        var parser = CreateParser(Tool("foo"), Tool("bar"));

        var result = parser.Parse("new(baz blammo): do a thing");

        var error = Assert.IsType<ParseErrorInstruction>(result);
        Assert.Equal("Unknown agent definition 'baz'. Available: foo, bar.", error.Message);
    }

    [Fact]
    public void UnrecognisedMessage_YieldsUnrecognisedPrefixError()
    {
        var parser = CreateParser(Tool("foo"));

        var result = parser.Parse("just some text with no prefix");

        var error = Assert.IsType<ParseErrorInstruction>(result);
        Assert.Equal(SubAgentMessageParser.UnrecognisedPrefixMessage, error.Message);
    }

    [Fact]
    public void MultilineBody_IsPreservedIntactAfterPrefix()
    {
        var parser = CreateParser(Tool("default"));
        var body = "First line of the prompt\nSecond line\n\n![image](data:image/png;base64,AAAA)\nThird line";

        var result = parser.Parse("new: " + body);

        var create = Assert.IsType<CreateSubAgentInstruction>(result);
        Assert.Equal(body, create.Prompt);
    }

    [Fact]
    public void MultilineBody_IsPreservedForRouteToSubAgent()
    {
        var parser = CreateParser(Tool("foo"));
        var body = "line one\nline two\nline three";

        var result = parser.Parse("foo-agent: " + body);

        var route = Assert.IsType<RouteToSubAgentInstruction>(result);
        Assert.Equal("foo-agent", route.Id);
        Assert.Equal(body, route.Message);
    }
}

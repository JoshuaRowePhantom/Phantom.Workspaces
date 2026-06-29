using Phantom.Workspaces.Agent.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void TryParseArguments_WithValidArgs_ReturnsParsedDefinition()
    {
        var success = Program.TryParseArguments(
            ["--provider", "echo", "--think", "low"],
            out var parseResult);

        Assert.True(success);
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult!.AgentDefinition);
    }

    [Fact]
    public void TryParseArguments_WithSessionId_ReturnsParsedSessionId()
    {
        var success = Program.TryParseArguments(
            ["--provider", "echo", "--session-id", "gui-session-123"],
            out var parseResult);

        Assert.True(success);
        Assert.NotNull(parseResult);
        Assert.Equal("gui-session-123", parseResult!.AgentSessionId);
    }

    [Fact]
    public void TryParseArguments_WithUnknownOption_ReturnsFalse()
    {
        var success = Program.TryParseArguments(
            ["--not-a-real-option"],
            out var parseResult);

        Assert.False(success);
        Assert.Null(parseResult);
    }

    [Fact]
    public void TryParseArguments_WithMissingSchemaFile_ReturnsFalseAndSetsParseError()
    {
        var success = Program.TryParseArguments(
            ["--agent-schema", "nonexistent-file.json"],
            out var parseResult);

        Assert.False(success);
        Assert.Null(parseResult);
        Assert.NotNull(Program.ParseError);
        Assert.NotEmpty(Program.ParseError!);
    }
}

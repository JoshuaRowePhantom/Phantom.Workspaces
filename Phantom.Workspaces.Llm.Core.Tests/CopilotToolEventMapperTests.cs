using System.Linq;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotToolEventMapperTests
{
    [Fact]
    public void MapToolStart_MapsCallIdNameAndParsedArguments()
    {
        var startEvent = new ToolExecutionStartEvent
        {
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-1",
                ToolName = "workspaces_entity_get",
                Arguments = JsonSerializer.Deserialize<JsonElement>("""{ "entity-id": "abc", "depth": 2 }"""),
            },
        };

        var call = CopilotToolEventMapper.MapToolStart(startEvent);

        Assert.Equal("call-1", call.CallId);
        Assert.Equal("workspaces_entity_get", call.Name);
        Assert.NotNull(call.Arguments);
        Assert.Equal("abc", ((JsonElement)call.Arguments!["entity-id"]!).GetString());
        Assert.Equal(2, ((JsonElement)call.Arguments!["depth"]!).GetInt32());
    }

    [Fact]
    public void MapToolStart_ParsesJsonStringArguments()
    {
        var startEvent = new ToolExecutionStartEvent
        {
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-2",
                ToolName = "do_thing",
                Arguments = JsonDocument.Parse("""{ "name": "value" }""").RootElement.Clone(),
            },
        };

        var call = CopilotToolEventMapper.MapToolStart(startEvent);

        Assert.Equal("value", ((JsonElement)call.Arguments!["name"]!).GetString());
    }

    [Fact]
    public void MapToolStart_FallsBackForMalformedArguments()
    {
        var startEvent = new ToolExecutionStartEvent
        {
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-3",
                ToolName = "do_thing",
                Arguments = JsonDocument.Parse("\"not json at all\"").RootElement.Clone(),
            },
        };

        var call = CopilotToolEventMapper.MapToolStart(startEvent);

        Assert.Equal("not json at all", ((JsonElement)call.Arguments!["arguments"]!).GetString());
    }

    [Fact]
    public void MapToolStart_UsesMcpToolName_WhenToolNameMissing()
    {
        var startEvent = new ToolExecutionStartEvent
        {
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-4",
                ToolName = null!,
                McpToolName = "mcp_tool",
            },
        };

        var call = CopilotToolEventMapper.MapToolStart(startEvent);

        Assert.Equal("mcp_tool", call.Name);
    }

    [Fact]
    public void MapToolComplete_MapsSuccessfulTextResult_PairedByCallId()
    {
        var completeEvent = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "the result text" },
            },
        };

        var result = CopilotToolEventMapper.MapToolComplete(completeEvent);

        Assert.Equal("call-1", result.CallId);
        Assert.Equal("the result text", result.Result);
    }

    [Fact]
    public void MapToolComplete_FallsBackToTextContent_WhenNoSummaryContent()
    {
        var completeEvent = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-5",
                Success = true,
                Result = new ToolExecutionCompleteResult
                {
                    Content = string.Empty,
                    Contents = [new ToolExecutionCompleteContentText { Type = "text", Text = "inner text" }],
                },
            },
        };

        var result = CopilotToolEventMapper.MapToolComplete(completeEvent);

        Assert.Equal("inner text", result.Result);
    }

    [Fact]
    public void MapToolComplete_MapsTerminalResult()
    {
        var completeEvent = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-6",
                Success = true,
                Result = new ToolExecutionCompleteResult
                {
                    Content = string.Empty,
                    Contents = [new ToolExecutionCompleteContentShellExit { Type = "shell_exit", ShellId = "shell-1", ExitCode = 0, OutputPreview = "done" }],
                },
            },
        };

        var result = CopilotToolEventMapper.MapToolComplete(completeEvent);

        var terminal = Assert.IsType<CopilotToolEventMapper.TerminalToolResult>(result.Result);
        Assert.Equal(0d, terminal.ExitCode);
        Assert.Equal("done", terminal.Text);
    }

    [Fact]
    public void MapToolComplete_SurfacesErrorMessage_WhenNotSuccessful()
    {
        var completeEvent = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-7",
                Success = false,
                Error = new ToolExecutionCompleteError { Code = "boom", Message = "it failed" },
            },
        };

        var result = CopilotToolEventMapper.MapToolComplete(completeEvent);

        Assert.Equal("call-7", result.CallId);
        Assert.Equal("it failed", result.Result);
    }

    [Fact]
    public void MapToolComplete_HasFallbackMessage_WhenErrorMissing()
    {
        var completeEvent = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-8",
                Success = false,
            },
        };

        var result = CopilotToolEventMapper.MapToolComplete(completeEvent);

        Assert.Equal("The tool call failed.", result.Result);
    }
}

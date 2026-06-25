using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Agent.Gui.Controls;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputBrowserCommandsTests
{
    [Fact]
    public void Update_SerializesTypeLocationAndContent()
    {
        var json = ChatOutputBrowserCommands.Update("msg-0", "after", "<div>hi</div>");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("update", root.GetProperty("type").GetString());
        Assert.Equal("msg-0", root.GetProperty("path").GetString());
        Assert.Equal("after", root.GetProperty("location").GetString());
        Assert.Equal("<div>hi</div>", root.GetProperty("content").GetString());
    }

    [Fact]
    public void Update_PreservesContentRequiringJsonEscaping()
    {
        var content = "<div class=\"x\">a & b < c</div>\n\"quoted\"";

        var json = ChatOutputBrowserCommands.Update("p", "replace", content);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(content, document.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Remove_SerializesTypeAndPath()
    {
        var json = ChatOutputBrowserCommands.Remove("run-2-c1");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("remove", root.GetProperty("type").GetString());
        Assert.Equal("run-2-c1", root.GetProperty("path").GetString());
    }

    [Fact]
    public void Scroll_SerializesTypeOnly()
    {
        var json = ChatOutputBrowserCommands.Scroll();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("scroll", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("path", out _));
    }

    [Fact]
    public void Theme_SerializesVariablesMap()
    {
        var variables = new Dictionary<string, string>
        {
            ["--chat-background"] = "#1e1e1e",
            ["--chat-foreground"] = "#e6e6e6",
        };

        var json = ChatOutputBrowserCommands.Theme(variables);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("theme", root.GetProperty("type").GetString());
        var emitted = root.GetProperty("variables");
        Assert.Equal("#1e1e1e", emitted.GetProperty("--chat-background").GetString());
        Assert.Equal("#e6e6e6", emitted.GetProperty("--chat-foreground").GetString());
    }
}

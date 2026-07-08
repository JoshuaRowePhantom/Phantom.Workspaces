using System.Collections.Generic;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class RecordingChatOutputHtmlSinkTests
{
    private readonly RecordingChatOutputHtmlSink sink = new();

    [Fact]
    public void UpdateContent_RecordsCommandWithCorrectPathLocationAndContent()
    {
        sink.UpdateContent("elem-1", ChatOutputUpdateLocation.Replace, "<p>hello</p>");

        Assert.Single(sink.Commands);
        var cmd = sink.Commands[0];
        Assert.Equal("elem-1", cmd.Path);
        Assert.Equal(ChatOutputUpdateLocation.Replace, cmd.Location);
        Assert.Equal("<p>hello</p>", cmd.Content);
    }

    [Fact]
    public void RemoveContent_RecordsCommandWithNullLocationAndNullContent()
    {
        sink.RemoveContent("elem-2");

        Assert.Single(sink.Commands);
        var cmd = sink.Commands[0];
        Assert.Equal("elem-2", cmd.Path);
        Assert.Null(cmd.Location);
        Assert.Null(cmd.Content);
    }

    [Fact]
    public void ScrollToBottom_RecordsNothing()
    {
        sink.ScrollToBottom();

        Assert.Empty(sink.Commands);
    }

    [Fact]
    public void Commands_ReturnedInInsertionOrder()
    {
        sink.UpdateContent("a", ChatOutputUpdateLocation.Append, "<span/>");
        sink.RemoveContent("b");
        sink.UpdateContent("c", ChatOutputUpdateLocation.Before, "<div/>");

        Assert.Equal(3, sink.Commands.Count);
        Assert.Equal("a", sink.Commands[0].Path);
        Assert.Equal("b", sink.Commands[1].Path);
        Assert.Equal("c", sink.Commands[2].Path);
    }

    [Fact]
    public void UpdateContent_and_RemoveContent_NullSemantics_RoundTrip()
    {
        sink.UpdateContent("x", ChatOutputUpdateLocation.After, "<em>hi</em>");
        sink.RemoveContent("x");

        var update = sink.Commands[0];
        var remove = sink.Commands[1];

        // Update command has non-null location and content
        Assert.NotNull(update.Location);
        Assert.NotNull(update.Content);

        // Remove command has null location and null content — distinguishable from update
        Assert.Null(remove.Location);
        Assert.Null(remove.Content);

        // Verify the two are structurally distinct despite sharing the same path
        Assert.NotEqual(update, remove);
    }

    [Fact]
    public void RecordingChatOutputHtmlSink_ImplementsIChatOutputHtmlSink()
    {
        // Verifies the class can be assigned to the interface (compile-time check surfaced at runtime)
        IChatOutputHtmlSink _ = sink;
        Assert.NotNull(_);
    }
}

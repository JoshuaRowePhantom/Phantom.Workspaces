using System.Collections.Generic;
using Phantom.Workspaces.Gui.Shared.Models;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;
using Xunit;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class TerminalSelectionModelTests
{
    [Fact]
    public void TerminalSelectionModel_LeftDrag_SelectsCharacterRange()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(5, 2));

        Assert.True(model.HasSelection);
        Assert.Equal(SelectionType.Character, model.Type);
        Assert.True(model.IsSelected(3, 1));
        Assert.False(model.IsSelected(6, 1));
    }

    [Fact]
    public void TerminalSelectionModel_AltDrag_SelectsRectangularRegion()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(2, 1), rectangular: true);
        model.ExtendSelection(new TerminalCell(5, 3));

        Assert.True(model.IsRectangular);
        Assert.True(model.IsSelected(3, 2));
        Assert.False(model.IsSelected(1, 2));
    }

    [Fact]
    public void TerminalSelectionModel_DoubleClick_SelectsWord()
    {
        var model = new TerminalSelectionModel();
        var lines = CreateLinesWithText("hello world");

        model.StartSelection(new TerminalCell(6, 0));
        model.ExpandToWords(lines);

        var selectedText = model.GetSelectedText(lines);
        Assert.Equal("world", selectedText);
    }

    [Fact]
    public void TerminalSelectionModel_TripleClick_SelectsLine()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(3, 1));
        model.ExpandToLines(5);

        Assert.Equal(SelectionType.Line, model.Type);
        Assert.True(model.IsSelected(0, 1));
    }

    [Fact]
    public void TerminalSelectionModel_ShiftClick_ExtendsSelection()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(10, 0));
        model.ExtendSelection(new TerminalCell(15, 0));

        Assert.True(model.IsSelected(12, 0));
    }

    [Fact]
    public void TerminalSelectionModel_CopyText_ReturnsSelectedCharacters()
    {
        var model = new TerminalSelectionModel();
        var lines = CreateLinesWithText("ABC", "DEF", "GHI");

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(2, 1));

        var selectedText = model.GetSelectedText(lines);
        Assert.Equal("ABC\nDE", selectedText);
    }

    [Fact]
    public void TerminalSelectionModel_CopyRectangularText_ReturnsColumnarLines()
    {
        var model = new TerminalSelectionModel();
        var lines = CreateLinesWithText("ABCDEF", "GHIJKL", "MNOPQR");

        model.StartSelection(new TerminalCell(2, 0), rectangular: true);
        model.ExtendSelection(new TerminalCell(4, 2));

        var selectedText = model.GetSelectedText(lines);
        Assert.Equal("CD\nIJ\nOP", selectedText);
    }

    [Fact]
    public void TerminalSelectionModel_Clear_ResetsHasSelection()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(5, 5));
        Assert.True(model.HasSelection);

        model.Clear();

        Assert.False(model.HasSelection);
        Assert.False(model.IsSelected(0, 0));
    }

    [Fact]
    public void TerminalSelectionModel_IsSelected_TrueWithinRange()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(2, 1));
        model.ExtendSelection(new TerminalCell(7, 3));

        Assert.True(model.IsSelected(5, 2));
    }

    [Fact]
    public void TerminalSelectionModel_IsSelected_FalseOutsideRange()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(2, 1));
        model.ExtendSelection(new TerminalCell(7, 3));

        Assert.False(model.IsSelected(1, 2));
        Assert.False(model.IsSelected(8, 2));
        Assert.False(model.IsSelected(5, 0));
        Assert.False(model.IsSelected(5, 4));
    }

    [Fact]
    public void TerminalSelectionModel_GetSelectedText_TrimsTrailingSpaces()
    {
        var model = new TerminalSelectionModel();
        var lines = CreateLinesWithText("AB   ", "CD   ");

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(4, 1));

        var selectedText = model.GetSelectedText(lines);
        Assert.Equal("AB\nCD", selectedText);
    }

    [Fact]
    public void TerminalSelectionModel_GetSelectedText_StripsNulCharacters()
    {
        var model = new TerminalSelectionModel();
        var lines = CreateLinesWithText("A\0B\0C");

        model.StartSelection(new TerminalCell(0, 0));
        model.ExtendSelection(new TerminalCell(4, 0));

        var selectedText = model.GetSelectedText(lines);
        Assert.Equal("ABC", selectedText);
    }

    [Fact]
    public void TerminalSelectionModel_IsSelected_HandlesReversedSelection()
    {
        var model = new TerminalSelectionModel();

        model.StartSelection(new TerminalCell(5, 2));
        model.ExtendSelection(new TerminalCell(1, 0));

        Assert.True(model.IsSelected(3, 1));
    }

    private static IReadOnlyList<TerminalLine> CreateLinesWithText(params string[] rows)
    {
        var vtc = new VirtualTerminalController();
        vtc.ResizeView(80, Math.Max(rows.Length, 24));
        var consumer = new DataConsumer(vtc);

        for (var i = 0; i < rows.Length; i++)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(rows[i]);
            consumer.Push(bytes);

            if (i < rows.Length - 1)
                consumer.Push(new byte[] { (byte)'\r', (byte)'\n' });
        }

        var lines = new List<TerminalLine>();
        for (var i = 0; i < rows.Length; i++)
        {
            var line = vtc.ViewPort.GetVisibleLine(i);
            if (line is not null)
                lines.Add(line);
        }

        return lines;
    }
}

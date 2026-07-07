using System;
using System.Collections.Generic;
using System.Text;
using VtNetCore.VirtualTerminal.Model;

namespace Phantom.Workspaces.Gui.Shared.Models;

/// <summary>
/// Tracks the user's native text selection state (anchor cell, active/end cell, selection type,
/// rectangular flag) and provides helpers to extract selected text. Pure C# model with no
/// Avalonia or UI dependencies.
/// </summary>
public sealed class TerminalSelectionModel
{
    private TerminalCell _anchor;
    private TerminalCell _active;

    public bool HasSelection { get; private set; }
    public SelectionType Type { get; private set; }
    public bool IsRectangular { get; private set; }

    /// <summary>
    /// Begin a new selection anchored at the given cell.
    /// </summary>
    public void StartSelection(TerminalCell anchor, bool rectangular = false)
    {
        _anchor = anchor;
        _active = anchor;
        HasSelection = true;
        Type = SelectionType.Character;
        IsRectangular = rectangular;
    }

    /// <summary>
    /// Update the active (end) cell — called on drag or shift-click.
    /// </summary>
    public void ExtendSelection(TerminalCell active)
    {
        _active = active;
        HasSelection = true;
    }

    /// <summary>
    /// Expand anchor+active to word boundaries. Word characters are alphanumeric + underscore.
    /// </summary>
    public void ExpandToWords(IReadOnlyList<TerminalLine> lines)
    {
        if (!HasSelection || lines.Count == 0)
            return;

        var row = _anchor.Row;
        if (row < 0 || row >= lines.Count)
            return;

        var line = lines[row];
        var col = _anchor.Col;

        var startCol = col;
        var endCol = col;

        while (startCol > 0 && IsWordChar(GetCharAt(line, startCol - 1)))
            startCol--;

        while (endCol < line.Count - 1 && IsWordChar(GetCharAt(line, endCol + 1)))
            endCol++;

        _anchor = new TerminalCell(startCol, row);
        _active = new TerminalCell(endCol, row);
        Type = SelectionType.Word;
    }

    /// <summary>
    /// Expand anchor+active to full lines.
    /// </summary>
    public void ExpandToLines(int totalRows)
    {
        if (!HasSelection)
            return;

        var minRow = Math.Min(_anchor.Row, _active.Row);
        var maxRow = Math.Max(_anchor.Row, _active.Row);

        minRow = Math.Max(0, minRow);
        maxRow = Math.Min(totalRows - 1, maxRow);

        _anchor = new TerminalCell(0, minRow);
        _active = new TerminalCell(int.MaxValue, maxRow);
        Type = SelectionType.Line;
    }

    /// <summary>
    /// Return the selected text. Rectangular mode joins columnar slices with '\n'.
    /// </summary>
    public string GetSelectedText(IReadOnlyList<TerminalLine> lines)
    {
        if (!HasSelection || lines.Count == 0)
            return string.Empty;

        var minRow = Math.Min(_anchor.Row, _active.Row);
        var maxRow = Math.Max(_anchor.Row, _active.Row);
        var minCol = Math.Min(_anchor.Col, _active.Col);
        var maxCol = Math.Max(_anchor.Col, _active.Col);

        minRow = Math.Max(0, minRow);
        maxRow = Math.Min(lines.Count - 1, maxRow);

        var sb = new StringBuilder();
        var isSingleRow = minRow == maxRow;

        for (var row = minRow; row <= maxRow; row++)
        {
            if (row >= lines.Count)
                break;

            var line = lines[row];
            string rowText;

            if (IsRectangular)
            {
                rowText = ExtractRowText(line, minCol, maxCol - 1);
            }
            else if (isSingleRow)
            {
                rowText = ExtractRowText(line, minCol, maxCol);
            }
            else
            {
                if (row == minRow)
                    rowText = ExtractRowText(line, minCol, int.MaxValue);
                else if (row == maxRow)
                    rowText = ExtractRowText(line, 0, maxCol - 1);
                else
                    rowText = ExtractRowText(line, 0, int.MaxValue);
            }

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(rowText);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Check if a given cell is within the current selection.
    /// </summary>
    public bool IsSelected(int col, int row)
    {
        if (!HasSelection)
            return false;

        var minRow = Math.Min(_anchor.Row, _active.Row);
        var maxRow = Math.Max(_anchor.Row, _active.Row);
        var minCol = Math.Min(_anchor.Col, _active.Col);
        var maxCol = Math.Max(_anchor.Col, _active.Col);

        if (IsRectangular)
        {
            return row >= minRow && row <= maxRow && col >= minCol && col <= maxCol;
        }
        else
        {
            return row >= minRow && row <= maxRow && col >= minCol && col <= maxCol;
        }
    }

    /// <summary>
    /// Clear the selection.
    /// </summary>
    public void Clear()
    {
        HasSelection = false;
        Type = SelectionType.Character;
        IsRectangular = false;
        _anchor = default;
        _active = default;
    }

    private static string ExtractRowText(TerminalLine line, int minCol, int maxCol)
    {
        var sb = new StringBuilder();
        var endCol = Math.Min(maxCol, line.Count - 1);

        for (var col = minCol; col <= endCol; col++)
        {
            var ch = GetCharAt(line, col);
            if (ch != '\0')
                sb.Append(ch);
        }

        return sb.ToString().TrimEnd();
    }

    private static char GetCharAt(TerminalLine line, int col)
    {
        if (col < 0 || col >= line.Count)
            return '\0';

        var cell = line[col];
        return cell?.Char ?? '\0';
    }

    private static bool IsWordChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }
}

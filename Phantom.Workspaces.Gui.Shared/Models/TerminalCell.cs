namespace Phantom.Workspaces.Gui.Shared.Models;

/// <summary>
/// Represents a cell coordinate in a terminal grid (zero-based column and row).
/// </summary>
public readonly record struct TerminalCell(int Col, int Row);

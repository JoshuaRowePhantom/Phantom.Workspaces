using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Controls;

public sealed class EntityCardHeaderPanel : Panel
{
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<EntityCardHeaderPanel, double>(nameof(ItemSpacing), 4);

    public static readonly StyledProperty<double> ActionsMinWidthProperty =
        AvaloniaProperty.Register<EntityCardHeaderPanel, double>(nameof(ActionsMinWidth), 100);

    public double ItemSpacing
    {
        get => this.GetValue(ItemSpacingProperty);
        set => this.SetValue(ItemSpacingProperty, value);
    }

    public double ActionsMinWidth
    {
        get => this.GetValue(ActionsMinWidthProperty);
        set => this.SetValue(ActionsMinWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = this.VisibleChildren().ToArray();
        if (visible.Length == 0)
        {
            return default;
        }

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var actionRows = this.BuildActionRows(visible, Math.Max(0, width), availableSize.Height, measure: true);
        var firstRowWidth = actionRows.Count == 0 ? 0 : actionRows[0].Width;
        var displayMinWidth = GetDisplayMinWidth(visible[0]);
        var headerWidth = Math.Max(displayMinWidth, width - firstRowWidth - (firstRowWidth > 0 ? this.ItemSpacing : 0));
        visible[0].Measure(new Size(headerWidth, availableSize.Height));

        var firstRowHeight = actionRows.Count == 0 ? 0 : actionRows[0].Height;
        var firstLineHeight = Math.Max(visible[0].DesiredSize.Height, firstRowHeight);
        var overflowHeight = actionRows.Skip(1).Sum(row => row.Height) + Math.Max(0, actionRows.Count - 1) * this.ItemSpacing;
        return new Size(width, firstLineHeight + overflowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = this.VisibleChildren().ToArray();
        if (visible.Length == 0)
        {
            return finalSize;
        }

        var actionRows = this.BuildActionRows(visible, finalSize.Width, finalSize.Height, measure: false);
        var firstRowWidth = actionRows.Count == 0 ? 0 : actionRows[0].Width;
        var firstRowHeight = actionRows.Count == 0 ? 0 : actionRows[0].Height;
        var displayMinWidth = GetDisplayMinWidth(visible[0]);
        var headerWidth = Math.Max(displayMinWidth, finalSize.Width - firstRowWidth - (firstRowWidth > 0 ? this.ItemSpacing : 0));
        var firstLineHeight = Math.Max(visible[0].DesiredSize.Height, firstRowHeight);

        visible[0].Arrange(new Rect(0, 0, headerWidth, firstLineHeight));

        if (actionRows.Count > 0)
        {
            this.ArrangeRow(actionRows[0], finalSize.Width, 0);
        }

        var y = firstLineHeight + this.ItemSpacing;
        foreach (var row in actionRows.Skip(1))
        {
            this.ArrangeRow(row, finalSize.Width, y);
            y += row.Height + this.ItemSpacing;
        }

        return finalSize;
    }

    private IReadOnlyList<ActionRow> BuildActionRows(Control[] visible, double width, double height, bool measure)
    {
        var rows = new List<ActionRow>();
        if (visible.Length <= 1)
        {
            return rows;
        }

        var displayMinWidth = GetDisplayMinWidth(visible[0]);

        var firstRowLimit = Math.Max(this.ActionsMinWidth, width - displayMinWidth);
        var rowLimit = Math.Max(this.ActionsMinWidth, width);

        var current = new List<Control>();
        var currentWidth = 0.0;
        var currentHeight = 0.0;
        var isFirstRow = true;

        for (var i = visible.Length - 1; i >= 1; i--)
        {
            var child = visible[i];
            if (measure)
            {
                child.Measure(new Size(width, height));
            }

            var childSize = child.DesiredSize;
            var proposedWidth = currentWidth + childSize.Width + (current.Count > 0 ? this.ItemSpacing : 0);
            var limit = isFirstRow ? firstRowLimit : rowLimit;
            if (current.Count > 0 && proposedWidth > limit)
            {
                rows.Add(new ActionRow(current, currentWidth, currentHeight));
                current = [];
                currentWidth = 0;
                currentHeight = 0;
                isFirstRow = false;
                proposedWidth = childSize.Width;
            }

            current.Insert(0, child);
            currentWidth = proposedWidth;
            currentHeight = Math.Max(currentHeight, childSize.Height);
        }

        if (current.Count > 0)
        {
            rows.Add(new ActionRow(current, currentWidth, currentHeight));
        }

        return rows;
    }

    private void ArrangeRow(ActionRow row, double finalWidth, double y)
    {
        var x = finalWidth - row.Width;
        foreach (var child in row.Children)
        {
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, y + (row.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + this.ItemSpacing;
        }
    }

    private IEnumerable<Control> VisibleChildren()
        => this.Children.Where(static child => child.IsVisible);

    private static double GetDisplayMinWidth(Control control)
    {
        var displayMinWidth = control.MinWidth;
        return double.IsNaN(displayMinWidth) || displayMinWidth <= 0 ? 100 : displayMinWidth;
    }

    private sealed record ActionRow(IReadOnlyList<Control> Children, double Width, double Height);
}



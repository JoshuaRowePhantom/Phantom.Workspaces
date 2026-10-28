using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>One forward flow, including the individual containers of flow item groups.</summary>
public sealed class EntityCardHeaderPanel : Panel
{
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<EntityCardHeaderPanel, double>(nameof(ItemSpacing), 4);

    // Kept for markup compatibility; actions no longer reserve space ahead of the title.
    public static readonly StyledProperty<double> ActionsMinWidthProperty =
        AvaloniaProperty.Register<EntityCardHeaderPanel, double>(nameof(ActionsMinWidth), 100);

    static EntityCardHeaderPanel()
    {
        AffectsMeasure<EntityCardHeaderPanel>(ItemSpacingProperty);
    }

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

    private readonly Dictionary<Control, Rect> placements = [];

    internal Rect Placement(Control child) => this.placements.GetValueOrDefault(child);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in this.Children)
        {
            child.Measure(availableSize);
        }

        return this.LayoutItems(availableSize.Width, measure: true);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        this.LayoutItems(finalSize.Width, measure: false);
        foreach (var child in this.Children)
        {
            if (child is ItemsControl { ItemsPanelRoot: EntityCardFlowItemsPanel group })
            {
                group.InvalidateArrange();
                child.Arrange(new Rect(finalSize));
            }
            else if (child.IsVisible)
            {
                child.Arrange(this.Placement(child));
            }
        }

        return finalSize;
    }

    private Size LayoutItems(double width, bool measure)
    {
        this.placements.Clear();
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        foreach (var child in this.FlowChildren())
        {
            if (measure)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }

            var size = child.DesiredSize;
            var itemWidth = Math.Min(width, size.Width);
            if (x > 0 && x + itemWidth > width)
            {
                y += rowHeight + this.ItemSpacing;
                x = 0;
                rowHeight = 0;
            }

            this.placements[child] = new Rect(x, y, itemWidth, size.Height);
            usedWidth = Math.Max(usedWidth, x + itemWidth);
            x += itemWidth + this.ItemSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(usedWidth, y + rowHeight);
    }

    private IEnumerable<Control> FlowChildren()
    {
        foreach (var child in this.Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (child is ItemsControl { ItemsPanelRoot: EntityCardFlowItemsPanel group })
            {
                foreach (var item in group.Children)
                {
                    if (item.IsVisible)
                    {
                        yield return item;
                    }
                }
            }
            else
            {
                yield return child;
            }
        }
    }
}

/// <summary>Keeps item templates and logical bindings while sharing the header's flow.</summary>
public sealed class EntityCardFlowItemsPanel : Panel
{
    public EntityCardFlowItemsPanel()
    {
        this.Children.CollectionChanged += (_, _) =>
            this.FindAncestorOfType<EntityCardHeaderPanel>()?.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in this.Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var header = this.FindAncestorOfType<EntityCardHeaderPanel>();
        if (header != null)
        {
            var origin = this.TranslatePoint(default, header) ?? default;
            foreach (var child in this.Children)
            {
                child.Arrange(header.Placement(child).Translate(new Vector(-origin.X, -origin.Y)));
            }
        }

        return finalSize;
    }
}

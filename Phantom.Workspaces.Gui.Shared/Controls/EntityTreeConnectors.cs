using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>Draws only realized immediate headers; never requests additional containers.</summary>
public sealed class EntityTreeConnectors : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<EntityTreeConnectors, IBrush?>(nameof(Brush), Brushes.Gray);

    private IReadOnlyList<Point> attachments = Array.Empty<Point>();
    private double railEnd;

    static EntityTreeConnectors()
    {
        AffectsRender<EntityTreeConnectors>(BrushProperty);
    }

    public EntityTreeConnectors()
    {
        this.IsHitTestVisible = false;
        this.ClipToBounds = true;
    }

    public IBrush? Brush
    {
        get => this.GetValue(BrushProperty);
        set => this.SetValue(BrushProperty, value);
    }

    public IReadOnlyList<Point> Attachments => this.attachments;

    public double RailEnd => this.railEnd;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.LayoutUpdated += this.OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        this.LayoutUpdated -= this.OnLayoutUpdated;
        this.attachments = Array.Empty<Point>();
        this.railEnd = 0;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var owner = this.FindAncestorOfType<TreeViewItem>();
        var points = new List<Point>();
        var end = 0.0;
        if (owner is { IsExpanded: true, ItemCount: > 0 })
        {
            end = this.Bounds.Height;
            foreach (var child in owner.GetRealizedContainers().OfType<TreeViewItem>())
            {
                var header = child.GetVisualDescendants().OfType<StackPanel>()
                    .FirstOrDefault(candidate => candidate.Classes.Contains("entity-card-tree-item")
                        && candidate.FindAncestorOfType<TreeViewItem>() == child);
                if (header?.TranslatePoint(new Point(0, header.Bounds.Height / 2), this) is { } point)
                {
                    points.Add(point);
                    if (owner.IndexFromContainer(child) == owner.ItemCount - 1)
                    {
                        end = point.Y;
                    }
                }
            }
        }

        if (!this.attachments.SequenceEqual(points) || this.railEnd != end)
        {
            this.attachments = points;
            this.railEnd = end;
            this.InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (this.Brush is not { } brush || this.railEnd <= 0)
        {
            return;
        }

        var pen = new Pen(brush, 2);
        context.DrawLine(pen, new Point(20, 0), new Point(20, this.railEnd));
        foreach (var point in this.attachments)
        {
            context.DrawLine(pen, new Point(20, point.Y), point);
        }
    }
}

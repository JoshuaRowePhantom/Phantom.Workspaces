using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public sealed class StickyScrollTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void ModeledEntityCardTemplate_ApplyLayer_MakesParentHeaderDrawAboveChildHeader()
    {
        var parentBranchHeader = new Border { Width = 220, Height = 40 };
        StickyItem.SetRow(parentBranchHeader, 0);
        parentBranchHeader.Classes.Add("entity-card");
        parentBranchHeader.Classes.Add("branch-header");

        var childBranchHeader = new Border { Width = 200, Height = 40 };
        childBranchHeader.Classes.Add("entity-card");
        childBranchHeader.Classes.Add("branch-header");

        var childItemPanel = new StackPanel();
        childItemPanel.Classes.Add("entity-card-tree-item");
        childItemPanel.Children.Add(childBranchHeader);

        // Mirrors the template's indented child region and forces overlap with the parent header.
        var childRegion = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        childRegion.RenderTransform = new TranslateTransform(0, -30);
        childRegion.Children.Add(childItemPanel);

        var parentItemPanel = new StackPanel();
        parentItemPanel.Classes.Add("entity-card-tree-item");
        parentItemPanel.Children.Add(parentBranchHeader);
        parentItemPanel.Children.Add(new Border { Width = 220, Height = 20 });
        parentItemPanel.Children.Add(childRegion);

        var content = new StackPanel();
        content.Children.Add(parentItemPanel);
        content.Children.Add(new Border { Width = 220, Height = 80 });

        var scrollViewer = new ScrollViewer
        {
            Width = 260,
            Height = 160,
            Content = content,
        };
        StickyScroll.SetIsEnabled(scrollViewer, true);

        var window = new Window
        {
            Width = 280,
            Height = 180,
            Content = scrollViewer,
        };

        window.Show();
        Assert.True(BoundsOverlap(parentBranchHeader, childBranchHeader, scrollViewer));
        StickyScroll.Engine.ApplyLayer(parentBranchHeader, scrollViewer, 1975);
        Assert.True(IsDrawnAbove(parentBranchHeader, childBranchHeader, scrollViewer));
        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DrawOrderComparator_UsesSiblingOrder_WhenZIndexIsEqual()
    {
        var first = new Border { Width = 100, Height = 40 };
        var second = new Border { Width = 100, Height = 40 };

        var canvas = new Canvas { Width = 120, Height = 120 };
        Canvas.SetTop(first, 0);
        Canvas.SetTop(second, 0);
        canvas.Children.Add(first);
        canvas.Children.Add(second);

        var window = new Window
        {
            Width = 130,
            Height = 130,
            Content = canvas,
        };

        window.Show();
        Assert.True(BoundsOverlap(first, second, canvas));
        Assert.True(IsDrawnAbove(second, first, canvas));
        Assert.False(IsDrawnAbove(first, second, canvas));
        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void LayerHelpers_ApplyLayer_MakesStickyDrawAboveNonSticky()
    {
        using var host = CreateHost();
        host.Window.Show();

        Assert.True(BoundsOverlap(host.StickyHeader, host.NonStickyHeader, host.ScrollViewer));

        StickyScroll.Engine.ApplyLayer(host.StickyHeader, host.ScrollViewer, 1975);

        Assert.Equal(1975, host.StickyHeader.GetValue(Panel.ZIndexProperty));
        Assert.Equal(1975, host.OwnerItem.GetValue(Panel.ZIndexProperty));
        Assert.Equal(1975, host.OwnerPresenter.GetValue(Panel.ZIndexProperty));
        Assert.True(IsDrawnAbove(host.StickyHeader, host.NonStickyHeader, host.ScrollViewer));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void LayerHelpers_ResetLayer_ClearsZIndex_AndPinnedClass()
    {
        using var host = CreateHost();
        host.Window.Show();

        StickyScroll.Engine.ApplyLayer(host.StickyHeader, host.ScrollViewer, 1888);
        host.StickyHeader.Classes.Add("sticky-pinned");
        StickyScroll.Engine.ResetLayer(host.StickyHeader, host.ScrollViewer);

        Assert.DoesNotContain("sticky-pinned", host.StickyHeader.Classes);
        Assert.Equal(0, host.StickyHeader.GetValue(Panel.ZIndexProperty));
        Assert.Equal(0, host.OwnerItem.GetValue(Panel.ZIndexProperty));
        Assert.Equal(0, host.OwnerPresenter.GetValue(Panel.ZIndexProperty));
    }

    private static StickyHost CreateHost()
    {
        var stickyHeader = new Border
        {
            Width = 160,
            Height = 40,
        };
        StickyItem.SetRow(stickyHeader, 0);

        var nonStickyHeader = new Border
        {
            Width = 160,
            Height = 40,
        };

        var ownerItem = CreateTreeItemWithHeader(stickyHeader);

        var ownerPresenter = new ContentPresenter
        {
            Content = ownerItem,
        };

        var nonStickyPresenter = new ContentPresenter
        {
            Content = nonStickyHeader,
        };

        var canvas = new Canvas();
        Canvas.SetTop(ownerPresenter, 0);
        Canvas.SetLeft(ownerPresenter, 0);
        Canvas.SetTop(nonStickyPresenter, 10);
        Canvas.SetLeft(nonStickyPresenter, 0);
        canvas.Children.Add(ownerPresenter);
        canvas.Children.Add(nonStickyPresenter);

        // Ensure measure/arrange gives enough room for both overlapping rows.
        canvas.Width = 200;
        canvas.Height = 200;

        var scrollViewer = new ScrollViewer
        {
            Width = 220,
            Height = 120,
            Content = canvas,
        };
        StickyScroll.SetIsEnabled(scrollViewer, true);

        var window = new Window
        {
            Width = 240,
            Height = 150,
            Content = scrollViewer,
        };

        return new StickyHost(window, scrollViewer, stickyHeader, nonStickyHeader, ownerItem, ownerPresenter);
    }

    private static TreeViewItem CreateTreeItemWithHeader(Control headerControl)
    {
        return new TreeViewItem
        {
            Template = new FuncControlTemplate<TreeViewItem>((_, _) =>
            {
                var panel = new StackPanel();
                panel.Children.Add(headerControl);
                return panel;
            }),
        };
    }

    private static bool BoundsOverlap(Control a, Control b, Visual relativeTo)
    {
        var aPoint = a.TranslatePoint(new Point(0, 0), relativeTo);
        var bPoint = b.TranslatePoint(new Point(0, 0), relativeTo);
        if (aPoint is null || bPoint is null)
        {
            return false;
        }

        var aRect = new Rect(aPoint.Value, a.Bounds.Size);
        var bRect = new Rect(bPoint.Value, b.Bounds.Size);
        return aRect.Intersects(bRect);
    }

    private static bool IsDrawnAbove(Control candidateTop, Control candidateBottom, Visual boundary)
    {
        var topPath = PathToBoundary(candidateTop, boundary);
        var bottomPath = PathToBoundary(candidateBottom, boundary);

        var sharedDepth = 0;
        var limit = Math.Min(topPath.Count, bottomPath.Count);
        while (sharedDepth < limit && ReferenceEquals(topPath[sharedDepth], bottomPath[sharedDepth]))
        {
            sharedDepth++;
        }

        if (sharedDepth == 0 || sharedDepth == limit)
        {
            return false;
        }

        var parent = topPath[sharedDepth - 1];
        var topBranch = topPath[sharedDepth];
        var bottomBranch = bottomPath[sharedDepth];
        var siblings = parent.GetVisualChildren().ToList();
        var topZ = (topBranch as Control)?.GetValue(Panel.ZIndexProperty) ?? 0;
        var bottomZ = (bottomBranch as Control)?.GetValue(Panel.ZIndexProperty) ?? 0;

        if (topZ != bottomZ)
        {
            return topZ > bottomZ;
        }

        var topIndex = siblings.IndexOf(topBranch);
        var bottomIndex = siblings.IndexOf(bottomBranch);
        return topIndex > bottomIndex;
    }

    private static List<Visual> PathToBoundary(Visual visual, Visual boundary)
    {
        var path = new List<Visual> { visual };
        var current = visual;
        while (!ReferenceEquals(current, boundary))
        {
            var parent = current.GetVisualParent();
            if (parent is null)
            {
                break;
            }

            path.Add(parent);
            current = parent;
        }

        path.Reverse();
        return path;
    }

    private sealed record StickyHost(
        Window Window,
        ScrollViewer ScrollViewer,
        Border StickyHeader,
        Border NonStickyHeader,
        TreeViewItem OwnerItem,
        Control OwnerPresenter) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
        }
    }
}

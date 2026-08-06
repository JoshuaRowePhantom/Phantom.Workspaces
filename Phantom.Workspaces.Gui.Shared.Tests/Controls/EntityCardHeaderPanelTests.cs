using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests.Controls;

[Collection("Avalonia")]
public sealed class EntityCardHeaderPanelTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_ArrangesLastActionAtTopRight()
    {
        var (panel, _, _, last) = Arrange(width: 220, actionWidth: 60, actionCount: 2);

        Assert.Equal(220, panel.Bounds.Width);
        Assert.Equal(220, last.Bounds.Right, precision: 1);
        Assert.Equal(0, last.Bounds.Y, precision: 1);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_OverflowActionsWrapBelowRightAnchored()
    {
        var (_, header, first, last) = Arrange(width: 180, actionWidth: 70, actionCount: 3);

        Assert.Equal(180, last.Bounds.Right, precision: 1);
        Assert.True(first.Bounds.Y >= header.Bounds.Bottom - 1);
        Assert.True(first.Bounds.X >= 0);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_DisplayNameUsesRemainingWidthBesideFirstRowActions()
    {
        var (_, header, _, last) = Arrange(width: 300, actionWidth: 80, actionCount: 1);

        Assert.Equal(0, header.Bounds.X, precision: 1);
        Assert.Equal(last.Bounds.X - 4, header.Bounds.Right, precision: 1);
    }

    // Issue #1264 (retry): explicit source-order / branch coverage for the custom panel.

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Wide_FirstRowActionsRightPackedInSourceOrder()
    {
        var (panel, header, actions) = ArrangeAll(width: 600, actionWidth: 40, actionCount: 4);

        // With ample width every action fits on the first row.
        foreach (var action in actions)
        {
            Assert.Equal(0, action.Bounds.Y, precision: 1);
        }

        // Visual source order is preserved left-to-right (I1, I2, A1, A2).
        for (var i = 1; i < actions.Length; i++)
        {
            Assert.True(
                actions[i].Bounds.X > actions[i - 1].Bounds.X,
                $"Action {i} (X={actions[i].Bounds.X}) must sit right of action {i - 1} (X={actions[i - 1].Bounds.X}).");
        }

        // The last source-order action is flush against the panel right edge and none of the
        // actions overlap the display-name column.
        Assert.Equal(600, actions[^1].Bounds.Right, precision: 1);
        Assert.True(
            actions[0].Bounds.X >= header.Bounds.Right - 0.5,
            $"First action (X={actions[0].Bounds.X}) must not overlap display column (right={header.Bounds.Right}).");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_EmptyActions_DisplayNameOccupiesFullWidth()
    {
        var (panel, header, _) = ArrangeAll(width: 300, actionWidth: 40, actionCount: 0);

        Assert.Equal(0, header.Bounds.X, precision: 1);
        Assert.Equal(300, header.Bounds.Width, precision: 1);
        Assert.Equal(header.Bounds.Height, panel.Bounds.Height, precision: 1);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_SingleAction_FitsOnFirstRow()
    {
        var (panel, header, actions) = ArrangeAll(width: 300, actionWidth: 40, actionCount: 1);
        var action = actions[0];

        Assert.Equal(0, action.Bounds.Y, precision: 1);
        Assert.Equal(300, action.Bounds.Right, precision: 1);
        Assert.Equal(0, header.Bounds.X, precision: 1);
        Assert.True(
            header.Bounds.Right <= action.Bounds.X + 0.5,
            $"Display column (right={header.Bounds.Right}) must occupy the remaining width beside the action (X={action.Bounds.X}).");
        Assert.Equal(Math.Max(header.Bounds.Height, action.Bounds.Height), panel.Bounds.Height, precision: 1);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Measure_ReservesActionsMinWidth()
    {
        // The display MinWidth (250) would leave only 50px beside it at width 300, but
        // ActionsMinWidth=100 reserves room so more than one action still packs onto the first row.
        var (panel, header, actions) = ArrangeAll(width: 300, actionWidth: 40, actionCount: 3, headerMinWidth: 250);

        var firstRow = actions.Where(action => Math.Abs(action.Bounds.Y) < 1).ToArray();
        Assert.True(
            firstRow.Length >= 2,
            $"ActionsMinWidth floor must reserve room for >=2 actions on the first row; got {firstRow.Length}.");
        Assert.Equal(300, actions[^1].Bounds.Right, precision: 1);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Narrow_EarlierActionsWrapBelowFullWidth()
    {
        var (panel, header, actions) = ArrangeAll(width: 200, actionWidth: 70, actionCount: 3, headerMinWidth: 100);
        var last = actions[^1];

        Assert.Equal(200, last.Bounds.Right, precision: 1);
        Assert.True(
            actions[0].Bounds.Y > last.Bounds.Y + 1,
            $"Earlier action (Y={actions[0].Bounds.Y}) must wrap onto a row below the last action (Y={last.Bounds.Y}).");

        // At least one overflow-row child extends leftward under the display-name column.
        Assert.Contains(
            actions,
            action => action.Bounds.Y > last.Bounds.Y + 1 && action.Bounds.X < header.Bounds.Right);
    }

    private static (EntityCardHeaderPanel Panel, Border Header, Border[] Actions) ArrangeAll(
        double width,
        double actionWidth,
        int actionCount,
        double headerMinWidth = 100,
        double actionHeight = 24)
    {
        var panel = new EntityCardHeaderPanel { Width = width, ItemSpacing = 4, ActionsMinWidth = 100 };
        var header = new Border { MinWidth = headerMinWidth, Height = 40 };
        panel.Children.Add(header);

        var actions = Enumerable.Range(0, actionCount)
            .Select(_ => new Border { Width = actionWidth, Height = actionHeight })
            .ToArray();
        foreach (var action in actions)
        {
            panel.Children.Add(action);
        }

        panel.Measure(new Size(width, 500));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        return (panel, header, actions);
    }

    private static (EntityCardHeaderPanel Panel, Border Header, Border First, Border Last) Arrange(
        double width,
        double actionWidth,
        int actionCount)
    {
        var panel = new EntityCardHeaderPanel { Width = width, ItemSpacing = 4, ActionsMinWidth = 100 };
        var header = new Border { MinWidth = 100, Height = 40 };
        panel.Children.Add(header);

        var actions = Enumerable.Range(0, actionCount)
            .Select(_ => new Border { Width = actionWidth, Height = 24 })
            .ToArray();
        foreach (var action in actions)
        {
            panel.Children.Add(action);
        }

        panel.Measure(new Size(width, 500));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        return (panel, header, actions[0], actions[^1]);
    }
}

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

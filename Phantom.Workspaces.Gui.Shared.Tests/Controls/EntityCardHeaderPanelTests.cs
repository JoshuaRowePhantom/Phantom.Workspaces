using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests.Controls;

[Collection("Avalonia")]
public sealed class EntityCardHeaderPanelTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Collections_IndividualContainersShareForwardFlow()
    {
        var header = new EntityCardHeaderPanel();
        header.Children.Add(new Border { Width = 100, Height = 40 });
        var tags = Group(30, 40);
        var shortcuts = Group(50, 60);
        header.Children.Add(tags);
        header.Children.Add(shortcuts);
        var window = new Window { Width = 200, Height = 300, Content = header };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var items = new[] { tags, shortcuts }.SelectMany(group =>
                ((EntityCardFlowItemsPanel)group.ItemsPanelRoot!).Children).ToArray();
            var points = items.Select(item => item.TranslatePoint(default, header)!.Value).ToArray();
            Assert.Equal(new Point(104, 0), points[0]);
            Assert.Equal(new Point(138, 0), points[1]);
            Assert.Equal(new Point(0, 44), points[2]);
            Assert.Equal(new Point(54, 44), points[3]);
            tags.ItemsSource = Array.Empty<double>();
            Dispatcher.UIThread.RunJobs();
            var shortcut = ((EntityCardFlowItemsPanel)shortcuts.ItemsPanelRoot!).Children[0];
            Assert.Equal(new Point(104, 0), shortcut.TranslatePoint(default, header));
        }
        finally
        {
            window.Close();
        }
    }

    private static ItemsControl Group(params double[] widths) => new()
    {
        ItemsSource = widths,
        ItemsPanel = new FuncTemplate<Panel?>(() => new EntityCardFlowItemsPanel()),
        ItemTemplate = new FuncDataTemplate<double>((width, _) => new Button { Width = width, Height = 24, Padding = default }),
    };

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Wide_ArrangesNameTagsAndShortcutsInSourceOrder()
    {
        var (panel, children) = Arrange(400, (100, 40), (30, 24), (40, 24), (50, 24));
        Assert.Equal(0, children[0].Bounds.X);
        Assert.Equal(104, children[1].Bounds.X);
        Assert.Equal(138, children[2].Bounds.X);
        Assert.Equal(182, children[3].Bounds.X);
        Assert.All(children, child => Assert.Equal(0, child.Bounds.Y));
        Assert.Equal(40, panel.Bounds.Height);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Narrow_WrapsIndividualActionsInSourceOrder()
    {
        var (_, children) = Arrange(180, (100, 40), (40, 24), (60, 32), (70, 24));
        Assert.Equal(new Rect(104, 0, 40, 24), children[1].Bounds);
        Assert.Equal(new Rect(0, 44, 60, 32), children[2].Bounds);
        Assert.Equal(new Rect(64, 44, 70, 24), children[3].Bounds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_LongName_WrapsWithoutOverlappingActions()
    {
        var panel = new EntityCardHeaderPanel();
        var title = new TextBlock { Text = new string('W', 120), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var action = new Border { Width = 60, Height = 24 };
        panel.Children.Add(title);
        panel.Children.Add(action);
        Layout(panel, 160);
        Assert.True(title.Bounds.Height > 40);
        Assert.True(action.Bounds.Top >= title.Bounds.Bottom + 4);
        Assert.True(title.Bounds.Right <= 160);
        Assert.True(action.Bounds.Right <= 160);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_VisibilityChanges_ReflowsRemainingItems()
    {
        var (panel, children) = Arrange(180, (100, 40), (40, 24), (60, 24));
        children[1].IsVisible = false;
        Layout(panel, 180);
        Assert.Equal(new Rect(104, 0, 60, 24), children[2].Bounds);
        children[1].IsVisible = true;
        Layout(panel, 180);
        Assert.Equal(44, children[2].Bounds.Y);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeaderPanel_Empty_HasNoReservedActionWidth()
    {
        var panel = new EntityCardHeaderPanel();
        Layout(panel, 80);
        Assert.Equal(0, panel.DesiredSize.Height);
        var name = new Border { Height = 20 };
        panel.Children.Add(name);
        Layout(panel, 80);
        Assert.True(name.Bounds.Width <= 80);
        Assert.Equal(20, panel.DesiredSize.Height);
    }

    private static (EntityCardHeaderPanel, Border[]) Arrange(double width, params (double Width, double Height)[] sizes)
    {
        var panel = new EntityCardHeaderPanel { ItemSpacing = 4 };
        var children = sizes.Select(size => new Border { Width = size.Width, Height = size.Height }).ToArray();
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }
        Layout(panel, width);
        return (panel, children);
    }

    private static void Layout(EntityCardHeaderPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }
}

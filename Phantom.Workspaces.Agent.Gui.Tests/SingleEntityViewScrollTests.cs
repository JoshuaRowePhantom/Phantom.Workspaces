using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

// Issue #1343: the single-entity View (EntityWorkspaceTabViewModel workspace tab) must scroll when
// the entity card is taller than the viewport. These headless layout tests realize the fixed
// single-entity shell chrome (ScrollViewer + Auto-row Grid + entity-card-shell ContentControl) and
// assert the ScrollViewer's Extent/Viewport diverge, the vertical scrollbar engages, the wheel
// scrolls, short content does not scroll, and the shell chrome does not clip overflow. A structural
// guard also protects the fix in the shipped DataTemplate.
public sealed class SingleEntityViewScrollTests
{
    private const string EntityCardSingleClass = "entity-card-single";

    [AvaloniaFact(Timeout = 15_000)]
    public void SingleEntityView_WhenContentTallerThanViewport_ScrollViewerExtentExceedsViewport()
    {
        var (window, scrollViewer) = BuildSingleEntityView(contentHeight: 2000);
        try
        {
            Assert.True(
                scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
                $"Extent {scrollViewer.Extent.Height} should exceed viewport {scrollViewer.Viewport.Height} for a tall card.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SingleEntityView_WhenContentTallerThanViewport_VerticalScrollBarIsVisible()
    {
        var (window, scrollViewer) = BuildSingleEntityView(contentHeight: 2000);
        try
        {
            var verticalBarVisible = window.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Any(bar => bar.Orientation == Orientation.Vertical && bar.IsEffectivelyVisible && bar.Bounds.Height > 0);

            Assert.True(verticalBarVisible, "A vertical scrollbar should be visible for an over-tall card.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SingleEntityView_WhenContentTallerThanViewport_MouseWheelChangesVerticalOffset()
    {
        var (window, scrollViewer) = BuildSingleEntityView(contentHeight: 2000);
        try
        {
            Assert.Equal(0, scrollViewer.Offset.Y);

            window.MouseWheel(new Point(150, 150), new Vector(0, -5));
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                scrollViewer.Offset.Y > 0,
                $"Mouse wheel should move the vertical offset; got {scrollViewer.Offset.Y}.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SingleEntityView_WhenContentFitsViewport_VerticalScrollBarStaysHidden()
    {
        var (window, scrollViewer) = BuildSingleEntityView(contentHeight: 40);
        try
        {
            Assert.True(
                scrollViewer.Extent.Height <= scrollViewer.Viewport.Height + 1,
                $"Short content should not overflow: extent {scrollViewer.Extent.Height}, viewport {scrollViewer.Viewport.Height}.");

            var verticalBarVisible = window.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Any(bar => bar.Orientation == Orientation.Vertical && bar.IsEffectivelyVisible && bar.Bounds.Height > 0);

            Assert.False(verticalBarVisible, "No vertical scrollbar should appear when content fits the viewport.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SingleEntityView_EntityCardShellChrome_DoesNotClipOverflowVertically()
    {
        var (window, scrollViewer) = BuildSingleEntityView(contentHeight: 2000);
        try
        {
            var shellBorders = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("entity-card-shell-border"))
                .ToArray();

            Assert.NotEmpty(shellBorders);
            Assert.All(shellBorders, border => Assert.False(
                border.ClipToBounds,
                "entity-card-shell-border must not clip the scrollable single-entity chain."));
        }
        finally
        {
            window.Close();
        }

        // Structural guard on the shipped DataTemplate: the immediate child of the vertically
        // scrolling ScrollViewer must not be top-aligned, which is what defeated the viewport
        // measure before #1343.
        var template = ExtractSingleEntityTemplate();
        Assert.DoesNotContain("VerticalAlignment=\"Top\"", template, StringComparison.Ordinal);
    }

    private static (Window Window, ScrollViewer ScrollViewer) BuildSingleEntityView(double contentHeight)
    {
        var content = new Border { Width = 200, Height = contentHeight };
        var shell = new ContentControl
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 160,
        };
        shell.Classes.Add("entity-card-shell");

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        grid.Children.Add(shell);

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
            Content = grid,
        };
        scrollViewer.Classes.Add(EntityCardSingleClass);

        var window = new Window { Width = 300, Height = 300, SizeToContent = SizeToContent.Manual };
        window.Styles.Add(LoadSharedStyles());
        window.Content = scrollViewer;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scrollViewer);
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        return (Avalonia.Styling.Styles)AvaloniaXamlLoader.Load(source, baseUri);
    }

    private static string ExtractSingleEntityTemplate()
    {
        var dataTemplates = ReadMainAppFile(Path.Combine("Templates", "WorkspaceDataTemplates.axaml"));
        var start = dataTemplates.IndexOf(
            "<DataTemplate DataType=\"vm:EntityWorkspaceTabViewModel\">",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the EntityWorkspaceTabViewModel DataTemplate to exist.");
        var end = dataTemplates.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the EntityWorkspaceTabViewModel DataTemplate to be closed.");
        return dataTemplates[start..(end + "</DataTemplate>".Length)];
    }

    private static string ReadMainAppFile(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces", relativePath));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}

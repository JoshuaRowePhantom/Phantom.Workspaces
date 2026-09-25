using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeReviewViewRenderingTests
{
    [AvaloniaFact]
    public async Task GitWorktreeReviewView_LongUnifiedLine_RendersSingleLayoutNoOverlap()
        => await VerifyLayoutAsync(sideBySide: false, width: 850);

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_LongSideBySideLine_RendersSingleLayoutNoOverlap()
        => await VerifyLayoutAsync(sideBySide: true, width: 850);

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_NarrowPane_DoesNotSuperimposeUnifiedAndSideBySideGrids()
    {
        await VerifyLayoutAsync(sideBySide: false, width: 425);
        await VerifyLayoutAsync(sideBySide: true, width: 425);
    }

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_ToggleSideBySide_OnlyOneLayoutVisibleAfterToggle()
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide: false, width: 660);
        await using (viewModel)
        {
            try
            {
                AssertLineLayout(rows, sideBySide: false);
                viewModel.SideBySide = true;
                await viewModel.CurrentRefresh!;
                viewModel.ApplyDiffs([CreateDiff(sideBySide: false)]);
                rows.SelectedIndex = 2;
                var before = Assert.IsType<GitDiffUnifiedLineRow>(viewModel.SelectedDiffRow);
                viewModel.ApplyDiffs([CreateDiff(sideBySide: true)]);
                window.UpdateLayout();
                AssertLineLayout(rows, sideBySide: true);
                Assert.Equal(before.Line.NewLineNumber, Assert.IsType<GitDiffSideBySideLineRow>(viewModel.SelectedDiffRow).Line.NewLineNumber);
                Assert.Same(viewModel.SelectedDiffRow, rows.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_RebuildFileDiffs_OnlyOneLayoutVisibleAfterRebuild()
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide: true, width: 660);
        await using (viewModel)
        {
            try
            {
                viewModel.ApplyDiffs([CreateDiff(sideBySide: true, lineCount: 3)]);
                window.UpdateLayout();
                AssertLineLayout(rows, sideBySide: true, expectedLines: 3);
                viewModel.ApplyDiffs([CreateDiff(sideBySide: false, lineCount: 4)]);
                window.UpdateLayout();
                AssertLineLayout(rows, sideBySide: false, expectedLines: 4);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_LargeDiff_RealizesOnlyVisibleRowContainers()
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide: false, width: 650, lineCount: 5000);
        await using (viewModel)
        {
            try
            {
                Assert.Equal(5002, viewModel.DiffRows.Count);
                Assert.InRange(rows.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
                Assert.InRange(rows.Bounds.Height, 1, window.Bounds.Height);
                rows.SelectedIndex = 4900;
                window.UpdateLayout();
                Assert.Same(viewModel.DiffRows[4900], viewModel.SelectedDiffRow);
                Assert.InRange(rows.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);

                rows.SelectedIndex = 0;
                window.UpdateLayout();
                var first = Assert.Single(rows.GetVisualDescendants().OfType<ListBoxItem>(),
                    item => ReferenceEquals(item.DataContext, viewModel.DiffRows[0]));
                Assert.True(first.Focus());
                var selectedNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnSelectionChanged(object? _, SelectionChangedEventArgs __)
                {
                    if (rows.SelectedIndex == 1)
                    {
                        selectedNext.TrySetResult();
                    }
                }

                rows.SelectionChanged += OnSelectionChanged;
                try
                {
                    window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "");
                    await selectedNext.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Assert.Same(viewModel.DiffRows[1], viewModel.SelectedDiffRow);
                }
                finally
                {
                    rows.SelectionChanged -= OnSelectionChanged;
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_RebuildPreservesScrollOffset()
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide: false, width: 650, lineCount: 5000);
        await using (viewModel)
        {
            try
            {
                var scroll = Assert.Single(rows.GetVisualDescendants().OfType<ScrollViewer>());
                scroll.Offset = new Avalonia.Vector(0, 240);
                window.UpdateLayout();
                Assert.InRange(scroll.Offset.Y, 230, 250);
                var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOffsetChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
                {
                    if (e.Property == ScrollViewer.OffsetProperty && scroll.Offset.Y >= 230)
                    {
                        restored.TrySetResult();
                    }
                }

                scroll.PropertyChanged += OnOffsetChanged;
                try
                {
                    viewModel.ApplyDiffs([CreateDiff(sideBySide: false, lineCount: 5000)]);
                    window.UpdateLayout();
                    if (scroll.Offset.Y < 230)
                    {
                        await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    }

                    Assert.InRange(scroll.Offset.Y, 230, 250);
                }
                finally
                {
                    scroll.PropertyChanged -= OnOffsetChanged;
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task GitWorktreeReviewView_LargeDiff_UiThreadRemainsResponsiveDuringRebuild()
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide: false, width: 650, lineCount: 5000);
        await using (viewModel)
        {
            try
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.BeforeDiffBuildAsync = token =>
                {
                    entered.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                viewModel.SideBySide = true;
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                viewModel.ApplyDiffs([CreateDiff(sideBySide: true, lineCount: 5000)]);
                var afterSwap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => afterSwap.SetResult(), Avalonia.Threading.DispatcherPriority.Background);
                await afterSwap.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                window.UpdateLayout();
                Assert.InRange(rows.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
                release.SetResult();
                await viewModel.CurrentRefresh!;
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyLayoutAsync(bool sideBySide, double width)
    {
        var (window, viewModel, rows) = await OpenAsync(sideBySide, width);
        await using (viewModel)
        {
            try
            {
                AssertLineLayout(rows, sideBySide);
                var scroll = Assert.Single(rows.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width, "Long NoWrap lines must be horizontally scrollable.");
                scroll.Offset = new Avalonia.Vector(180, scroll.Offset.Y);
                window.UpdateLayout();
                Assert.True(scroll.Offset.X > 0);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void AssertLineLayout(ListBox rows, bool sideBySide, int expectedLines = 1)
    {
        var realizedLines = rows.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is GitDiffLineRow).ToArray();
        Assert.Equal(expectedLines, realizedLines.Length);
        foreach (var item in realizedLines)
        {
            var layouts = item.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.Name is "UnifiedLineLayout" or "SideBySideLineLayout").ToArray();
            Assert.Single(layouts);
            Assert.Equal(sideBySide ? "SideBySideLineLayout" : "UnifiedLineLayout", layouts[0].Name);
            Assert.True(layouts[0].IsEffectivelyVisible);
        }
    }

    private static async Task<(Window Window, GitWorktreeReviewWorkspaceTabViewModel ViewModel, ListBox Rows)>
        OpenAsync(bool sideBySide, double width, int lineCount = 1)
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "target-branch": "main"
            }
            """);
        var entity = new SubscribedEntityViewModel(new EntitySnapshot
        {
            EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = [],
        });
        var viewModel = new GitWorktreeReviewWorkspaceTabViewModel(
            entity, TaskScheduler.FromCurrentSynchronizationContext())
        {
            Id = "review-render-test",
            Title = "Review",
            Entity = entity,
        };
        await viewModel.CurrentRefresh!;
        viewModel.ApplyDiffs([CreateDiff(sideBySide, lineCount)]);

        var review = new GitWorktreeReviewView { DataContext = viewModel };
        var window = new Window { Width = width, Height = 340, Content = review };
        window.Show();
        window.UpdateLayout();
        return (window, viewModel, review.FindControl<ListBox>("DiffRowsList")!);
    }

    private static GitDiffViewModel CreateDiff(bool sideBySide, int lineCount = 1)
        => new()
        {
            RelativePath = "long-line.cs",
            LinesAdded = lineCount,
            LinesRemoved = 0,
            SideBySide = sideBySide,
            Hunks =
            [
                new GitDiffHunk
                {
                    OldStart = 1,
                    NewStart = 1,
                    Lines = Enumerable.Range(1, lineCount).Select(number =>
                        new GitDiffLine
                        {
                            Kind = GitDiffLineKind.Added,
                            NewLineNumber = number,
                            Content = lineCount == 1 ? new string('x', 1500) + number : $"row {number}",
                        }).ToArray(),
                },
            ],
        };
}

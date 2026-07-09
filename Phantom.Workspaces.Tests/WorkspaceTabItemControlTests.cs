using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceTabItemControlTests
{
    private sealed class StubViewModel
    {
        public bool IsRunning { get; init; }
        public bool IsInteresting { get; init; }
        public string? WorkspaceName { get; init; }
        public string TabTitle { get; init; } = "Agent Tab";
    }

    private static (Window window, ProgressBar progressBar) ShowControlInWindow(bool isRunning)
    {
        var control = new WorkspaceTabItemControl
        {
            DataContext = new StubViewModel { IsRunning = isRunning }
        };

        var window = new Window { Content = control };
        window.Show();

        var progressBar = window.GetVisualDescendants().OfType<ProgressBar>().First();
        return (window, progressBar);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceTabItemControl_Always_ProgressBarHasRunningIndicatorAgentClass()
    {
        var (window, progressBar) = ShowControlInWindow(isRunning: false);
        try
        {
            Assert.Contains("running-indicator-agent", progressBar.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceTabItemControl_WhenIsRunningIsTrue_ProgressBarIsIndeterminate()
    {
        var (window, progressBar) = ShowControlInWindow(isRunning: true);
        try
        {
            Assert.True(progressBar.IsIndeterminate);
        }
        finally
        {
            window.Close();
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceTabItemControl_WhenIsRunningIsTrue_ShowsBrainGlyph()
    {
        var (window, progressBar) = ShowControlInWindow(isRunning: true);
        try
        {
            var glyph = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.NotNull(glyph);
            Assert.Equal("🧠", glyph.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceTabItemControl_WhenIsRunningIsFalse_ProgressBarIsNotIndeterminate()
    {
        var (window, progressBar) = ShowControlInWindow(isRunning: false);
        try
        {
            Assert.False(progressBar.IsIndeterminate);
        }
        finally
        {
            window.Close();
        }
    }
}

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using System.Linq;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShellTabViewModelControlBarTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void ControlBar_StopAndRestartButtons_HaveTooltips()
    {
        var tab = new ShellTabViewModel(new ShellTabViewModelTests.RecordingTerminalSession())
        {
            Id = "cb-test",
            Title = "cb-test",
        };

        var templates = new WorkspaceDataTemplates();
        var template = templates.Cast<IDataTemplate>().First(t => t.Match(tab));
        var control = template.Build(tab);
        Assert.NotNull(control);
        control!.DataContext = tab;

        var host = new ContentControl { Content = control };
        host.Measure(new Avalonia.Size(1000, 600));
        host.Arrange(new Avalonia.Rect(0, 0, 1000, 600));

        var buttons = control.GetLogicalDescendants().OfType<Button>().ToList();
        var buttonTooltips = buttons
            .Select(b => new { Content = b.Content?.ToString(), Tip = ToolTip.GetTip(b)?.ToString() })
            .ToList();

        var stop = buttonTooltips.FirstOrDefault(b => b.Content == "■");
        var restart = buttonTooltips.FirstOrDefault(b => b.Content == "↻");
        Assert.NotNull(stop);
        Assert.NotNull(restart);
        Assert.False(string.IsNullOrWhiteSpace(stop!.Tip));
        Assert.False(string.IsNullOrWhiteSpace(restart!.Tip));
    }
}
